namespace NightElf.Kernel.Core;

public interface INonCriticalEventBus
{
    IDisposable Subscribe<TEvent>(
        Func<TEvent, CancellationToken, Task> handler)
        where TEvent : class;

    Task PublishAsync<TEvent>(
        TEvent eventData,
        CancellationToken cancellationToken = default)
        where TEvent : class;
}

public sealed class InMemoryNonCriticalEventBus : INonCriticalEventBus
{
    private readonly Lock _subscriptionsLock = new();
    private readonly Dictionary<Type, List<Subscription>> _subscriptions = new();

    public IDisposable Subscribe<TEvent>(
        Func<TEvent, CancellationToken, Task> handler)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription(
            typeof(TEvent),
            (eventData, cancellationToken) => handler((TEvent)eventData, cancellationToken),
            this);

        lock (_subscriptionsLock)
        {
            if (!_subscriptions.TryGetValue(typeof(TEvent), out var handlers))
            {
                handlers = [];
                _subscriptions[typeof(TEvent)] = handlers;
            }

            handlers.Add(subscription);
        }

        return subscription;
    }

    public Task PublishAsync<TEvent>(
        TEvent eventData,
        CancellationToken cancellationToken = default)
        where TEvent : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(eventData);

        Subscription[] handlers;

        lock (_subscriptionsLock)
        {
            if (!_subscriptions.TryGetValue(typeof(TEvent), out var subscriptions) || subscriptions.Count == 0)
            {
                return Task.CompletedTask;
            }

            handlers = [.. subscriptions];
        }

        return PublishCoreAsync(handlers, eventData, cancellationToken);
    }

    private async Task PublishCoreAsync(
        IReadOnlyList<Subscription> handlers,
        object eventData,
        CancellationToken cancellationToken)
    {
        foreach (var handler in handlers)
        {
            await handler.Handler(eventData, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_subscriptionsLock)
        {
            if (!_subscriptions.TryGetValue(subscription.EventType, out var handlers))
            {
                return;
            }

            handlers.Remove(subscription);

            if (handlers.Count == 0)
            {
                _subscriptions.Remove(subscription.EventType);
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private InMemoryNonCriticalEventBus? _owner;

        public Subscription(
            Type eventType,
            Func<object, CancellationToken, Task> handler,
            InMemoryNonCriticalEventBus owner)
        {
            EventType = eventType ?? throw new ArgumentNullException(nameof(eventType));
            Handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public Type EventType { get; }

        public Func<object, CancellationToken, Task> Handler { get; }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Unsubscribe(this);
        }
    }
}
