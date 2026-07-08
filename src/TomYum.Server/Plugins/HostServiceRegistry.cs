using System.Collections.Concurrent;

namespace TomYum.Server.Plugins;

/// <summary>
/// Backs <see cref="TomYum.Plugins.Abstractions.IPluginContext.GetHostService{TService}"/>: a
/// type-keyed map of host services the server chooses to expose to plugins (e.g.
/// <see cref="TomYum.Plugins.Abstractions.IHostPlayerView"/>). The service type must live in
/// <c>TomYum.Plugins.Abstractions</c> so plugins can reference it.
/// </summary>
public sealed class HostServiceRegistry
{
    private readonly ConcurrentDictionary<Type, object> _services = new();

    /// <summary>Publishes <paramref name="instance"/> under <typeparamref name="TService"/>.</summary>
    public void Register<TService>(TService instance)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        _services[typeof(TService)] = instance;
    }

    /// <summary>Resolves a published host service, or <c>null</c> when none is registered.</summary>
    public TService? Get<TService>()
        where TService : class
        => _services.TryGetValue(typeof(TService), out var service) ? (TService)service : null;
}
