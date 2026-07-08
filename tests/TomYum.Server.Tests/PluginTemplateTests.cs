using System.IO;

using TomYum.Plugins.Loader;

using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// Guards the shipped plugin template (the <c>templates/plugin-template</c> submodule). The
/// loose-source shape is compiled at runtime by Roslyn with NO global usings, so a missing
/// <c>using</c> silently breaks it for every author who copies it. This compiles the real
/// template folder the same way the host does and asserts it loads.
/// </summary>
public class PluginTemplateTests
{
    [Fact]
    public async Task Loose_source_template_compiles_and_loads()
    {
        var source = FindTemplateLooseSourceDir();
        var root = Path.Combine(Path.GetTempPath(), "tomyum-template-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(root, "template");
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));

        var loader = new PluginLoader(allowLooseSource: true);
        try
        {
            var plugins = await loader.LoadAllAsync(root);

            // If the template fails to compile, the loader logs + skips it and this is empty.
            var plugin = Assert.Single(plugins);
            Assert.Equal("example.my-loose-plugin", plugin.Manifest.Id);

            await loader.UnloadAsync(plugin);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FindTemplateLooseSourceDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "templates", "plugin-template", "loose-source");
            if (File.Exists(Path.Combine(candidate, "plugin.json")))
                return candidate;
        }

        throw new DirectoryNotFoundException(
            "Could not locate templates/plugin-template/loose-source (is the submodule checked out?).");
    }
}
