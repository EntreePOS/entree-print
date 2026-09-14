using System.Collections.Concurrent;
using System.Threading.Channels;
using EntreePrintPlugin.Models;

namespace EntreePrintPlugin.Services;

public sealed class EventBroadcaster : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Channel<PrintEvent>> _subscribers = new();
    private readonly PluginSettings _settings;
    private readonly object _inventoryGate = new();
    private bool _inventoryComplete = true;
    public DurableEventLog? History { get; }
    public EventBroadcaster(string? directory = null, PluginSettings? settings = null)
    { History = directory is null ? null : new DurableEventLog(directory); _settings = settings ?? new PluginSettings(); }
    internal EventBroadcaster(DurableEventLog history, PluginSettings settings)
    { History = history; _settings = settings; }
    internal int SubscriberCount => _subscribers.Count;

    internal T CaptureInventory<T>(Func<T> capture)
    {
        lock (_inventoryGate)
        {
            if (!_inventoryComplete) throw new CommandException("EVENTS_UNAVAILABLE", "Printer inventory publication is pending. Retry the checkpoint.");
            return capture();
        }
    }

    internal void PublishInventory(IReadOnlyList<PrinterStatusRecord> previous, IReadOnlyList<PrinterStatusRecord> current, Action commit)
    {
        lock (_inventoryGate)
        {
            _inventoryComplete = false;
            foreach (var removed in previous.Where(old => !current.Any(item => item.Name.Equals(old.Name, StringComparison.OrdinalIgnoreCase))))
            {
                var data = new { name = removed.Name, removed = true };
                History?.Append("printer", removed.Name, 0, DateTimeOffset.UtcNow, data);
                Broadcast("printer", data);
            }
            foreach (var printer in current) Publish("printer", printer);
            commit();
            _inventoryComplete = true;
            Broadcast("printers", current);
        }
    }

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
            History?.Append(type, printer.Name, 0, printer.UpdatedAt, V2ApiService.PrinterView(_settings, printer));
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
