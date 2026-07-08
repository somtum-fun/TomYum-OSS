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

    public TService? GetHostService<TService>()
        where TService : class
        => _hostServices.Get<TService>();
}
