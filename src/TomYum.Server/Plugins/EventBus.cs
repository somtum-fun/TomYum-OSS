using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using TomYum.Plugins.Abstractions;

namespace TomYum.Server.Plugins;

/// <summary>
/// The shared publish/subscribe hub behind <see cref="IEventBus"/>. One singleton: the host raises
/// events on the hot path (login, chat, action-change, score-submit), plugins subscribe. Handler
/// exceptions are logged and swallowed so one bad plugin can't break a request. For an
/// <see cref="IVetoableEvent"/>, dispatch stops as soon as a handler vetoes.
/// </summary>
public sealed class EventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<Subscription>> _byType = new();
    private readonly object _lock = new();
    private readonly ILogger<EventBus> _log;

    public EventBus(ILogger<EventBus> log) => _log = log;

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : IPluginEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription(typeof(TEvent), (e, ct) => handler((TEvent)e, ct), this);
        lock (_lock)
        {
            var list = _byType.GetOrAdd(typeof(TEvent), static _ => []);
            list.Add(subscription);
        }

        return subscription;
    }

    /// <summary>Whether any plugin listens for <typeparamref name="TEvent"/> (the website feed skips the rest).</summary>
    public bool HasSubscribers<TEvent>()
        where TEvent : IPluginEvent
    {
        lock (_lock)
            return _byType.TryGetValue(typeof(TEvent), out var list) && list.Count > 0;
    }

    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IPluginEvent
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (!_byType.TryGetValue(typeof(TEvent), out var list))
            return;

        Subscription[] snapshot;
        lock (_lock)
        {
            snapshot = list.ToArray();
        }

        var vetoable = @event as IVetoableEvent;
        foreach (var subscription in snapshot)
        {
            try
            {
                await subscription.Handler(@event, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "plugin event handler for {Event} threw", typeof(TEvent).Name);
            }

            if (vetoable is { IsVetoed: true })
                break;
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_lock)
        {
            if (_byType.TryGetValue(subscription.EventType, out var list))
                list.Remove(subscription);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private EventBus? _owner;

        public Subscription(Type eventType, Func<IPluginEvent, CancellationToken, Task> handler, EventBus owner)
        {
            EventType = eventType;
            Handler = handler;
            _owner = owner;
        }

        public Type EventType { get; }

        public Func<IPluginEvent, CancellationToken, Task> Handler { get; }

        public void Dispose()
        {
            _owner?.Remove(this);
            _owner = null;
        }
    }
}
