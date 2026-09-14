using System.Collections.Concurrent;
using System.Threading.Channels;
using EntreePrintPlugin.Models;

namespace EntreePrintPlugin.Services;

public sealed class EventBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Channel<PrintEvent>> _subscribers = new();

    public ChannelReader<PrintEvent> Subscribe(out Guid subscriptionId)
    {
        subscriptionId = Guid.NewGuid();
        var channel = Channel.CreateBounded<PrintEvent>(new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
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
        var printEvent = new PrintEvent
        {
            Type = type,
            Data = data
        };

        foreach (var subscriber in _subscribers)
        {
            subscriber.Value.Writer.TryWrite(printEvent);
        }
    }
}
