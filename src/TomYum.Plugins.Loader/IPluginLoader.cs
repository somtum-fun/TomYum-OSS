using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.Loader;

/// <summary>
/// Discovers and loads plugins from the runtime <c>plugins/</c> directory: compiled
/// <c>.dll</c> assemblies (each in its own collectible <c>AssemblyLoadContext</c> for
/// hot-unload) and loose source folders (<c>folder/*.cs</c> compiled with Roslyn at
/// runtime). See ADR-0007 and <c>memories/features/plugins.md</c>.
/// </summary>
/// <remarks>Implementation is out of scope for the scaffolding phase — contract only.</remarks>
public interface IPluginLoader
{
    /// <summary>
    /// Scans <paramref name="pluginsDirectory"/>, loads every discovered plugin, and
    /// returns the loaded instances (not yet initialized).
    /// </summary>
    Task<IReadOnlyList<IPlugin>> LoadAllAsync(string pluginsDirectory, CancellationToken cancellationToken = default);

    /// <summary>Unloads a previously loaded plugin and its assembly load context.</summary>
    Task UnloadAsync(IPlugin plugin, CancellationToken cancellationToken = default);
}
