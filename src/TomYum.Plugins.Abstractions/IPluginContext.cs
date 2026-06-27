namespace TomYum.Plugins.Abstractions;

/// <summary>
/// The host-provided surface a plugin uses to integrate with TomYum. Passed to
/// <see cref="IPlugin.InitializeAsync"/>. Everything a plugin is allowed to touch
/// flows through this context, keeping plugins decoupled from core internals.
/// </summary>
/// <remarks>
/// DAN Mode uses the event bus (action-change, spectate-frame, score-submission veto),
/// the player state bag, and the bot-spectate control. Session uses the auth/session
/// store and the HTTP route registry. See <c>memories/features/dan-mode.md</c> and
/// <c>memories/features/session.md</c>.
/// </remarks>
public interface IPluginContext
{
    /// <summary>Strongly-typed, scoped configuration for this plugin (bound from <c>plugins:&lt;id&gt;</c>).</summary>
    IPluginConfig Config { get; }

    /// <summary>Structured logger scoped to the plugin id.</summary>
    IPluginLogger Logger { get; }

    /// <summary>Subscribe to and publish server events (the hook surface).</summary>
    IEventBus Events { get; }

    /// <summary>Register chat/console commands the plugin handles.</summary>
    ICommandRegistry Commands { get; }

    /// <summary>Register authenticated HTTP routes (used by Session's frontend API surface).</summary>
    IHttpRouteRegistry Http { get; }

    /// <summary>Resolve a host service the plugin host has explicitly exposed to plugins.</summary>
    /// <typeparam name="TService">A service type published by the host (e.g. a bot-spectate controller).</typeparam>
    /// <returns>The service instance, or <c>null</c> when the host does not expose it.</returns>
    TService? GetHostService<TService>() where TService : class;
}

/// <summary>Read access to a plugin's own configuration subtree.</summary>
public interface IPluginConfig
{
    /// <summary>Reads a value by key, returning <paramref name="defaultValue"/> when absent.</summary>
    T? Get<T>(string key, T? defaultValue = default);

    /// <summary>Binds the whole plugin config subtree to a strongly-typed options object.</summary>
    TOptions Bind<TOptions>() where TOptions : new();
}

/// <summary>Minimal logging abstraction so plugins never depend on a concrete logging package.</summary>
public interface IPluginLogger
{
    /// <summary>Writes an informational message.</summary>
    void Info(string message);

    /// <summary>Writes a warning.</summary>
    void Warn(string message);

    /// <summary>Writes an error, optionally with an exception.</summary>
    void Error(string message, Exception? exception = null);
}
