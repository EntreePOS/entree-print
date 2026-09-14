using System.Security.Cryptography;
using System.Text.Json;

namespace EntreePrintPlugin.Services;

public sealed record DurablePrintEvent(long Sequence, string Type, string EntityId, long Version, DateTimeOffset OccurredAt, JsonElement Data);
public sealed record EventPage(IReadOnlyList<DurablePrintEvent> Items, long NextCursor, long LatestCursor);

// Event delivery is at least once. A job's pending event is retained in the same
// atomic record as its state until this bounded replay log has committed it.
public sealed class DurableEventLog : IDisposable
{
    public const int MaximumEvents = 2000;
    public const int MaximumBytes = 16_000_000;
    private readonly string _path;
    private readonly FileStream _owner;
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly int _byteLimit;
    private List<DurablePrintEvent> _events = [];
    private long _sequence;
    private bool _disposed;

    public DurableEventLog(string directory) : this(directory, MaximumEvents, MaximumBytes) { }
    internal DurableEventLog(string directory, int capacity, int byteLimit)
    {
        if (capacity < 1 || byteLimit < 4096) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity; _byteLimit = byteLimit;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(Path.GetFullPath(directory), "events.json");
        var ownerPath = Path.Combine(directory, "owner.lock");
        var existing = File.Exists(ownerPath);
        _owner = new FileStream(ownerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (!File.Exists(_path))
            {
                if (existing) throw new InvalidDataException("Event history is missing; refusing to reuse event identities.");
                WriteState(0, []);
                return;
            }
            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > byteLimit) throw new InvalidDataException("Event history exceeds its storage limit.");
            var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
            var state = JsonSerializer.Deserialize<LogState>(bytes) ?? throw new InvalidDataException("Unreadable event history.");
            if (state.Schema != 1 || state.Events is null || state.Events.Length > capacity || state.Sequence < 0 ||
                state.Digest != Digest(state.Sequence, state.Events) ||
                (state.Events.Length == 0 ? state.Sequence != 0 : state.Events[^1].Sequence != state.Sequence))
                throw new InvalidDataException("Invalid event history.");
            long previous = 0;
            foreach (var entry in state.Events)
            {
                if (entry.Sequence <= previous || (previous != 0 && entry.Sequence != previous + 1) ||
                    entry.Version < 1 || string.IsNullOrWhiteSpace(entry.EntityId) || entry.Type is not ("job" or "printer"))
                    throw new InvalidDataException("Invalid event identity or ordering.");
                previous = entry.Sequence;
            }
            _events = [.. state.Events]; _sequence = state.Sequence;
        }
        catch { _owner.Dispose(); throw; }
    }

    public DurablePrintEvent Append(string type, string entityId, long version, DateTimeOffset occurredAt, object data)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (type is not ("job" or "printer") || string.IsNullOrWhiteSpace(entityId) || version < 0)
                throw new ArgumentException("A supported event and entity identity are required.");
            var payload = JsonSerializer.SerializeToElement(data);
            // Retrying a job's pending notification does not invent another state.
            var prior = version == 0 ? null : _events.LastOrDefault(item => item.Type == type && item.EntityId == entityId && item.Version == version);
            if (prior is not null)
            {
                if (!JsonElement.DeepEquals(prior.Data, payload)) throw new InvalidDataException("An event version has conflicting data.");
                return prior;
            }
            var next = checked(_sequence + 1);
            var entry = new DurablePrintEvent(next, type, entityId, version == 0 ? next : version, occurredAt, payload);
            var size = JsonSerializer.SerializeToUtf8Bytes(entry).Length;
            if (size > Math.Min(2_000_000, _byteLimit - 1024)) throw new IOException("The status event exceeds its storage limit.");
            var pending = new List<DurablePrintEvent>(_events) { entry };
            var bytes = pending.Sum(item => JsonSerializer.SerializeToUtf8Bytes(item).Length + 1);
            while (pending.Count > _capacity || bytes > _byteLimit - 1024)
            {
                bytes -= JsonSerializer.SerializeToUtf8Bytes(pending[0]).Length + 1;
                pending.RemoveAt(0);
            }
            var rows = pending.ToArray();
            WriteState(next, rows);
            _events = pending; _sequence = next;
            return entry;
        }
    }

    public EventPage ReadAfter(long cursor, int limit = 100)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (cursor < 0 || cursor > _sequence || limit is < 1 or > 100)
                throw new CommandException("EVENT_CURSOR_INVALID", "The event cursor or page limit is invalid.");
            if (_events.Count != 0 && cursor < _events[0].Sequence - 1)
                throw new CommandException("EVENT_CURSOR_EXPIRED", "Event history was pruned. Refresh current printers and jobs before resuming.");
            var items = _events.Where(item => item.Sequence > cursor).Take(limit).ToArray();
            return new EventPage(items, items.Length == 0 ? cursor : items[^1].Sequence, _sequence);
        }
    }

    public long LatestCursor { get { lock (_gate) return _sequence; } }
    private void WriteState(long sequence, DurablePrintEvent[] rows)
    {
        var encoded = JsonSerializer.SerializeToUtf8Bytes(new LogState(1, sequence, rows, Digest(sequence, rows)));
        if (encoded.Length > _byteLimit) throw new IOException("Event history exceeds its storage limit.");
        var temporary = _path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { file.Write(encoded); file.Flush(flushToDisk: true); }
        File.Move(temporary, _path, overwrite: true);
    }
    private static string Digest(long sequence, DurablePrintEvent[] events) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { sequence, events })));
    public void Dispose() { lock (_gate) { _disposed = true; _owner.Dispose(); } }
    private sealed record LogState(int Schema, long Sequence, DurablePrintEvent[] Events, string Digest);
}
