using TomYum.Plugins.Abstractions;

namespace TomYum.Server.Plugins;

/// <summary>
/// The per-plugin <see cref="IPluginContext"/> the host hands to <see cref="IPlugin.InitializeAsync"/>.
/// Composes the plugin's scoped config/logger/command+route facades, the shared event bus, an
/// optional trusted data handle, and the host-service lookup.
/// </summary>
internal sealed class PluginContext : IPluginContext
{
    private readonly HostServiceRegistry _hostServices;

    public PluginContext(
        IPluginConfig config,
        IPluginLogger logger,
        IEventBus events,
        ICommandRegistry commands,
        IHttpRouteRegistry http,
        IPluginDataAccess? data,
        HostServiceRegistry hostServices)
    {
        Config = config;
        Logger = logger;
        Events = events;
        Commands = commands;
        Http = http;
        Data = data;
        _hostServices = hostServices;
    }

    public IPluginConfig Config { get; }

    public IPluginLogger Logger { get; }

    public IEventBus Events { get; }

    public ICommandRegistry Commands { get; }

    public IHttpRouteRegistry Http { get; }

    public IPluginDataAccess? Data { get; }

    /// <remarks>
    /// <see cref="IServiceProvider"/> (every host service, internals included) is handed out only to
    /// trusted plugins, the ones with <see cref="Data"/>: e.g. a compiled Discord bot built against the host.
    /// </remarks>
    public TService? GetHostService<TService>()
        where TService : class
        => typeof(TService) == typeof(IServiceProvider) && Data is null ? null : _hostServices.Get<TService>();

    public void PublishHostService<TService>(TService service)
        where TService : class
    {
        // The host's own (IServiceProvider and the host views) can't be replaced by a plugin.
        if (typeof(TService) == typeof(IServiceProvider) || typeof(TService).Assembly != typeof(IPluginContext).Assembly)
            throw new ArgumentException("A plugin can only publish a service as a type from TomYum.Plugins.Abstractions.", nameof(service));
        _hostServices.Register(service);
    }
}
