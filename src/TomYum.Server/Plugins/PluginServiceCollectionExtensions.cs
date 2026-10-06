using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TomYum.Plugins.Abstractions;

namespace TomYum.Server.Plugins;

/// <summary>DI wiring for the plugin host (ADR-0007).</summary>
public static class PluginServiceCollectionExtensions
{
    /// <summary>
    /// Registers the plugin host: the shared event bus, command registry, HTTP route table,
    /// host-service registry, the loaded-plugin registry, the read-only player view and online
    /// counts exposed to plugins, and the <see cref="PluginHostService"/> that loads + drives plugins on start.
    /// Requires <c>DatabaseOptions</c> to be registered (used for trusted plugin data access).
    /// </summary>
    public static IServiceCollection AddTomYumPlugins(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new PluginHostOptions();
        configuration.GetSection(PluginHostOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        services.AddSingleton<EventBus>();
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<EventBus>());
        services.AddSingleton<PluginCommandRegistry>();
        services.AddSingleton<PluginRouteTable>();
        services.AddSingleton<HostServiceRegistry>();
        services.AddSingleton<LoadedPluginRegistry>();
        services.AddSingleton<IHostPlayerView, HostPlayerView>();
        services.AddSingleton<IHostOnlineCounts, HostOnlineCountsView>();
        services.AddSingleton<IHostMessenger, HostMessenger>();
        services.AddSingleton<IHostSpectating, HostSpectating>();

        services.AddHostedService<PluginHostService>();

        return services;
    }
}
