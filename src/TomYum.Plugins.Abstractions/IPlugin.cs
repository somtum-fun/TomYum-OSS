namespace TomYum.Plugins.Abstractions;

/// <summary>
/// Identity and metadata for a plugin, surfaced to operators and other plugins.
/// </summary>
/// <param name="Id">Stable unique id, e.g. <c>tomyum.dan-mode</c>. Used for config and dependency resolution.</param>
/// <param name="Name">Human-readable display name.</param>
/// <param name="Version">Semantic version of the plugin build.</param>
/// <param name="Authors">Free-form author/maintainer string.</param>
/// <param name="Description">One-line summary of what the plugin does.</param>
public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string Authors,
    string Description);

/// <summary>
/// The entry point every TomYum plugin implements. The loader discovers an
/// <see cref="IPlugin"/> in a compiled <c>.dll</c> or a runtime-compiled loose
/// <c>.cs</c> folder, instantiates it, and drives its lifecycle.
/// </summary>
/// <remarks>
/// Plugins reference ONLY <c>TomYum.Plugins.Abstractions</c> — never core or
/// infrastructure projects. DAN Mode and Session ship as plugins built on this
/// contract. See <c>memories/architecture/decisions/0007-plugin-model.md</c> and
/// <c>memories/features/plugins.md</c>.
/// </remarks>
public interface IPlugin
{
    /// <summary>Identity/metadata for this plugin.</summary>
    PluginManifest Manifest { get; }

    /// <summary>
    /// Called once after construction. The plugin registers event hooks, commands,
    /// HTTP routes, and background work against <paramref name="context"/>.
    /// </summary>
    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called once during graceful shutdown or hot-unload. The plugin releases
    /// resources; the loader then unloads its <c>AssemblyLoadContext</c>.
    /// </summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}
