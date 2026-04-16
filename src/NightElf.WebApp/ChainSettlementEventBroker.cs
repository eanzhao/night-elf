using System.Threading.Channels;

using NightElf.Kernel.Core;

namespace NightElf.WebApp;

public sealed class ChainSettlementEventBroker : IDisposable
{
    private const int MaxBufferedEvents = 256;

    private readonly Lock _lock = new();
    private readonly Dictionary<int, Channel<ChainSettlementEventEnvelope>> _subscribers = new();
    private readonly Queue<ChainSettlementEventEnvelope> _recentEvents = new();
    private readonly IDisposable _subscription;
    private int _nextSubscriberId;
    private bool _disposed;

    public ChainSettlementEventBroker(INonCriticalEventBus eventBus)
    {
        ArgumentNullException.ThrowIfNull(eventBus);

        _subscription = eventBus.Subscribe<ChainSettlementEventEnvelope>((eventEnvelope, _) =>
        {
            Publish(eventEnvelope);
            return Task.CompletedTask;
        });
    }

    public ChainSettlementEventSubscription Subscribe()
    {
        ThrowIfDisposed();

        Channel<ChainSettlementEventEnvelope> channel;
        ChainSettlementEventEnvelope[] snapshot;
        int subscriberId;

        lock (_lock)
        {
            subscriberId = _nextSubscriberId++;
            channel = Channel.CreateUnbounded<ChainSettlementEventEnvelope>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                });
            _subscribers.Add(subscriberId, channel);
            snapshot = _recentEvents.ToArray();
        }

        return new ChainSettlementEventSubscription(
            snapshot,
            channel.Reader,
            () => Unsubscribe(subscriberId));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subscription.Dispose();

        Channel<ChainSettlementEventEnvelope>[] channels;
        lock (_lock)
        {
            channels = _subscribers.Values.ToArray();
            _subscribers.Clear();
            _recentEvents.Clear();
        }

        foreach (var channel in channels)
        {
            channel.Writer.TryComplete();
        }
    }

    private void Publish(ChainSettlementEventEnvelope eventEnvelope)
    {
        ArgumentNullException.ThrowIfNull(eventEnvelope);

        Channel<ChainSettlementEventEnvelope>[] channels;
        lock (_lock)
        {
            _recentEvents.Enqueue(eventEnvelope);
            while (_recentEvents.Count > MaxBufferedEvents)
            {
                _recentEvents.Dequeue();
            }

            channels = _subscribers.Values.ToArray();
        }

        foreach (var channel in channels)
        {
            channel.Writer.TryWrite(eventEnvelope);
        }
    }

    private void Unsubscribe(int subscriberId)
    {
        Channel<ChainSettlementEventEnvelope>? channel = null;

        lock (_lock)
        {
            if (_subscribers.Remove(subscriberId, out channel))
            {
                channel.Writer.TryComplete();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed class ChainSettlementEventSubscription : IDisposable
{
    private readonly Action _disposeAction;
    private int _disposed;

    public ChainSettlementEventSubscription(
        IReadOnlyList<ChainSettlementEventEnvelope> snapshot,
        ChannelReader<ChainSettlementEventEnvelope> reader,
        Action disposeAction)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _disposeAction = disposeAction ?? throw new ArgumentNullException(nameof(disposeAction));
    }

    public IReadOnlyList<ChainSettlementEventEnvelope> Snapshot { get; }

    public ChannelReader<ChainSettlementEventEnvelope> Reader { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _disposeAction();
        }
    }
}
