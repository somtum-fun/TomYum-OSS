using Microsoft.Extensions.Configuration;

using TomYum.Plugins.Abstractions;

namespace TomYum.Server.Plugins;

/// <summary>
/// Adapts a plugin's configuration subtree (<c>Plugins:&lt;id&gt;</c>) to the plugin-facing
/// <see cref="IPluginConfig"/>. An empty section is fine — reads return defaults.
/// </summary>
internal sealed class PluginConfig : IPluginConfig
{
    private readonly IConfiguration _section;

    public PluginConfig(IConfiguration section) => _section = section;

    public T? Get<T>(string key, T? defaultValue = default)
    {
        var child = _section.GetSection(key);
        return child.Exists() ? child.Get<T>() ?? defaultValue : defaultValue;
    }

    public TOptions Bind<TOptions>() where TOptions : new()
    {
        var options = new TOptions();
        _section.Bind(options);
        return options;
    }
}
