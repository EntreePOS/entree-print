using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace EntreePrintPlugin.Services;

public static class EventStream
{
    public static string Cursor(string serviceId, long sequence) => serviceId + ":" + sequence.ToString(CultureInfo.InvariantCulture);

    public static object Checkpoint(HttpRequest request, EventBroadcaster events, JobStore jobs, V2ApiService api, ServiceIdentity identity)
    {
        if (request.Query.Count != 0) throw new CommandException("FIELD_UNSUPPORTED", "Event checkpoints accept no query options.");
        jobs.FlushPendingEvents();
        var history = History(events);
        return events.CaptureInventory(() => new { identity.ServiceId,
            printers = api.CachedInventory(), cursor = Cursor(identity.ServiceId, history.LatestCursor) });
    }

    public static IResult Open(HttpRequest request, EventBroadcaster events, JobStore jobs, V2ApiService api, ServiceIdentity identity)
    {
        if (request.Query.Keys.Any(key => key != "cursor")) throw new CommandException("FIELD_UNSUPPORTED", "The event stream accepts only cursor.");
        var query = request.Query["cursor"];
        var header = request.Headers["Last-Event-ID"];
        if (query.Count > 1 || header.Count > 1 || (query.Count != 0 && header.Count != 0))
            throw new CommandException("EVENT_CURSOR_INVALID", "Provide one cursor, in the query or Last-Event-ID header.");
        jobs.FlushPendingEvents();
        var history = History(events);
        var supplied = query.Count != 0 ? query[0] : header.Count != 0 ? header[0] : null;
        var cursor = supplied is null ? history.LatestCursor : ParseCursor(supplied, identity.ServiceId);
        history.ReadAfter(cursor); // Validate before a streaming response starts.
        return TypedResults.ServerSentEvents(Read(events, history, api, identity.ServiceId, cursor, request.HttpContext.RequestAborted));
    }

    private static DurableEventLog History(EventBroadcaster events) => events.History
        ?? throw new CommandException("EVENTS_UNAVAILABLE", "Durable event history is unavailable.");

    internal static long ParseCursor(string value, string serviceId)
    {
        var prefix = serviceId + ":";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
            throw new CommandException("SERVICE_MISMATCH", "The event cursor belongs to another print service.");
        var sequence = value[prefix.Length..];
        if (sequence.Length == 0 || sequence.Length > 19 || (sequence.Length > 1 && sequence[0] == '0') ||
            !long.TryParse(sequence, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
            throw new CommandException("EVENT_CURSOR_INVALID", "The event cursor is invalid.");
        return result;
    }

    private static async IAsyncEnumerable<SseItem<object>> Read(EventBroadcaster events, DurableEventLog history,
        V2ApiService api, string serviceId, long cursor, [EnumeratorCancellation] CancellationToken token)
    {
        var wake = events.Subscribe(out var subscription);
        try
        {
            var caughtUp = false;
            yield return new SseItem<object>(new { serviceId, cursor = Cursor(serviceId,cursor) }, "ready");
            while (!token.IsCancellationRequested)
            {
                EventPage? page = null;
                try { page = history.ReadAfter(cursor); }
                catch (CommandException error) when (error.Code == "EVENT_CURSOR_EXPIRED") { }
                if (page is null)
                {
                    // No id: a gap notice must not acknowledge events the client missed.
                    yield return new SseItem<object>(new { serviceId, code = "EVENT_CURSOR_EXPIRED" }, "reset");
                    yield break;
                }
                foreach (var entry in page.Items)
                {
                    token.ThrowIfCancellationRequested();
                    yield return new SseItem<object>(api.EventView(entry), entry.Type) { EventId = Cursor(serviceId, entry.Sequence) };
                    cursor = entry.Sequence;
                }
                if (cursor < page.LatestCursor) continue;
                while (wake.TryRead(out _)) { }
                if (history.LatestCursor > cursor) continue;
                if (!caughtUp)
                {
                    yield return new SseItem<object>(new { serviceId, cursor = Cursor(serviceId, cursor) }, "caught-up");
                    caughtUp = true;
                }
                var closed = false;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                try { closed = !await wake.WaitToReadAsync(deadline.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (Exception error) when (error is IOException or ChannelClosedException) { closed = true; }
                if (closed)
                {
                    yield return new SseItem<object>(new { serviceId, code = "EVENT_STREAM_GAP" }, "reconnect");
                    yield break;
                }
                if (deadline.IsCancellationRequested)
                    yield return new SseItem<object>(new { serviceId }, "heartbeat");
            }
        }
        finally { events.Unsubscribe(subscription); }
    }
}
