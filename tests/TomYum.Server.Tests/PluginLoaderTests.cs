using System.IO;

using TomYum.Plugins.Loader;

using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// The loader compiles a loose <c>.cs</c> plugin folder with Roslyn, loads it into a collectible
/// context, and instantiates its <see cref="TomYum.Plugins.Abstractions.IPlugin"/> — the loose-source
/// path from ADR-0007. (The compiled-<c>.dll</c> path shares the same ALC/instantiate code.)
/// </summary>
public class PluginLoaderTests
{
    private const string ManifestJson =
        @"{ ""id"": ""test.sample"", ""name"": ""Sample"", ""version"": ""1.2.3"", ""authors"": ""tester"", ""description"": ""d"", ""entryPoint"": ""SamplePlugin"" }";

    private const string PluginSource = @"
using System.Threading;
using System.Threading.Tasks;
using TomYum.Plugins.Abstractions;

public sealed class SamplePlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new(""test.sample"", ""Sample"", ""1.2.3"", ""tester"", ""d"");
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}";

    [Fact]
    public async Task LoadAllAsync_compiles_and_loads_a_loose_source_plugin()
    {
        var (root, _) = WriteLoosePlugin();
        try
        {
            var loader = new PluginLoader(allowLooseSource: true);
            var plugins = await loader.LoadAllAsync(root);

            var plugin = Assert.Single(plugins);
            Assert.Equal("test.sample", plugin.Manifest.Id);
            Assert.Equal("1.2.3", plugin.Manifest.Version);

            await loader.UnloadAsync(plugin);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAllAsync_skips_loose_source_when_disabled()
    {
        var (root, _) = WriteLoosePlugin();
        try
        {
            var loader = new PluginLoader(allowLooseSource: false);
            var plugins = await loader.LoadAllAsync(root);

            Assert.Empty(plugins);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (string Root, string PluginDir) WriteLoosePlugin()
    {
        var root = Path.Combine(Path.GetTempPath(), "tomyum-plugins-" + Guid.NewGuid().ToString("N"));
        var pluginDir = Path.Combine(root, "sample");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), ManifestJson);
        File.WriteAllText(Path.Combine(pluginDir, "code.cs"), PluginSource);
        return (root, pluginDir);
    }
}
