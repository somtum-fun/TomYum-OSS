namespace TomYum.Server.Plugins;

/// <summary>
/// Operator configuration for the plugin host (config section <c>Plugins</c>). Per-plugin settings
/// live under <c>Plugins:&lt;plugin-id&gt;</c> and are handed to each plugin as its
/// <see cref="TomYum.Plugins.Abstractions.IPluginConfig"/>.
/// </summary>
public sealed class PluginHostOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Plugins";

    /// <summary>Directory scanned for plugins, relative to the repo root (or absolute).</summary>
    public string Directory { get; set; } = "plugins";

    /// <summary>When false, loose <c>.cs</c> plugin folders are ignored (only compiled <c>.dll</c> load).</summary>
    public bool AllowLooseSource { get; set; } = true;

    /// <summary>
    /// Plugin ids granted trusted raw DB access (<see cref="TomYum.Plugins.Abstractions.IPluginContext.Data"/>).
    /// Anything not listed gets <c>Data == null</c> and must stay within events/commands/HTTP (ADR-0007).
    /// </summary>
    public List<string> Trusted { get; set; } = [];
}
