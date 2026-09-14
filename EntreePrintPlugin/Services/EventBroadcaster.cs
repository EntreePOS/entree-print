using System.Collections.Concurrent;
using System.Threading.Channels;
using EntreePrintPlugin.Models;

namespace EntreePrintPlugin.Services;

public sealed class EventBroadcaster : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Channel<PrintEvent>> _subscribers = new();
    public DurableEventLog? History { get; }
    public EventBroadcaster(string? directory = null) => History = directory is null ? null : new DurableEventLog(directory);

    public ChannelReader<PrintEvent> Subscribe(out Guid subscriptionId)
    {
        subscriptionId = Guid.NewGuid();
        var channel = Channel.CreateBounded<PrintEvent>(new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        _subscribers[subscriptionId] = channel;
        return channel.Reader;
    }

    public void Unsubscribe(Guid subscriptionId)
    {
        if (_subscribers.TryRemove(subscriptionId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public void Publish(string type, object? data)
    {
        if (data is PrinterStatusRecord printer && type == "printer")
            History?.Append(type, printer.Name, 0, printer.UpdatedAt, printer);
        Broadcast(type, data);
    }

    public void PublishJob(JobRecord job, AcceptedCommand command)
    {
        History?.Append("job", job.Id, job.Version, job.UpdatedAt,
            new { job, command.IdempotencyKey, command.RequestDigest, command.MetadataJson, command.ReprintOf });
        Broadcast("job", job with { });
    }

    private void Broadcast(string type, object? data)
    {
        var printEvent = new PrintEvent
        {
            Type = type,
            Data = data
        };

        foreach (var subscriber in _subscribers)
        {
            if (!subscriber.Value.Writer.TryWrite(printEvent) && _subscribers.TryRemove(subscriber.Key, out var dropped))
                dropped.Writer.TryComplete(new IOException("Event consumer fell behind. Resume from durable history."));
        }
    }

    public void Dispose()
    {
        foreach (var id in _subscribers.Keys) Unsubscribe(id);
        History?.Dispose();
    }
}
