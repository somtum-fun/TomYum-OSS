using TomYum.Plugins.Abstractions;

namespace TomYum.Server.Plugins;

/// <summary>
/// Tracks the manifests of currently-loaded plugins so <c>!plugins</c> can list them. Populated by
/// <see cref="PluginHostService"/> at startup/shutdown.
/// </summary>
public sealed class LoadedPluginRegistry
{
    private readonly List<PluginManifest> _manifests = [];
    private readonly object _lock = new();

    /// <summary>Replaces the loaded set (called after load and on shutdown).</summary>
    public void SetLoaded(IEnumerable<PluginManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        lock (_lock)
        {
            _manifests.Clear();
            _manifests.AddRange(manifests);
        }
    }

    /// <summary>A snapshot of the loaded plugins' manifests.</summary>
    public IReadOnlyList<PluginManifest> Loaded
    {
        get
        {
            lock (_lock)
            {
                return _manifests.ToArray();
            }
        }
    }
}
