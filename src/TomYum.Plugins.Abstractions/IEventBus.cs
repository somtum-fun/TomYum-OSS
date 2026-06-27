namespace TomYum.Plugins.Abstractions;

/// <summary>
/// The publish/subscribe hook surface. Plugins subscribe to server events; some
/// events are <em>vetoable</em>, letting a plugin reject an in-flight operation
/// (e.g. DAN Mode rejecting a score that breaks dan rules).
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Subscribes a handler to events of type <typeparamref name="TEvent"/>.
    /// Handlers run in registration order; a vetoable event stops dispatch once vetoed.
    /// </summary>
    /// <returns>A disposable that unsubscribes the handler when disposed.</returns>
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : IPluginEvent;

    /// <summary>Publishes an event to all subscribers. Used by the host; plugins may raise custom events too.</summary>
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IPluginEvent;
}

/// <summary>Marker for anything dispatched over <see cref="IEventBus"/>.</summary>
public interface IPluginEvent
{
}

/// <summary>
/// An event whose outcome a handler may block. The host inspects <see cref="IsVetoed"/>
/// after dispatch and aborts the operation when set.
/// </summary>
public interface IVetoableEvent : IPluginEvent
{
    /// <summary>Whether a handler has vetoed the operation.</summary>
    bool IsVetoed { get; }

    /// <summary>Why the operation was vetoed, surfaced to the player/log when present.</summary>
    string? VetoReason { get; }

    /// <summary>Marks the operation as rejected with a human-readable reason.</summary>
    void Veto(string reason);
}
