using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.Loader;

/// <summary>
/// Discovers and loads plugins from the runtime <c>plugins/</c> directory (ADR-0007):
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Compiled: each top-level <c>plugins/*.dll</c> is loaded into its own collectible
/// <see cref="PluginLoadContext"/> and scanned for a public parameterless <see cref="IPlugin"/>.</item>
/// <item>Compiled with dependencies: <c>plugins/&lt;name&gt;/&lt;name&gt;.dll</c> (a folder without a <c>plugin.json</c>),
/// its own NuGet dependencies next to it (resolved through its <c>.deps.json</c>); host assemblies it
/// doesn't ship come from the host.</item>
/// <item>Loose source: each immediate subdirectory containing a <c>plugin.json</c> has its
/// <c>*.cs</c> files compiled in memory with Roslyn (referencing the host's assemblies), then
/// loaded the same way. Gated by <c>allowLooseSource</c>.</item>
/// </list>
/// A failed plugin is logged and skipped — one bad plugin never stops the others from loading.
/// </remarks>
public sealed class PluginLoader : IPluginLoader
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly bool _allowLooseSource;
    private readonly Action<string, Exception?>? _log;
    private readonly ConcurrentDictionary<IPlugin, PluginLoadContext> _contexts = new(ReferenceEqualityComparer.Instance);

    /// <summary>Creates the loader.</summary>
    /// <param name="allowLooseSource">When false, loose <c>.cs</c> folders are ignored (only <c>.dll</c> plugins load).</param>
    /// <param name="log">Optional sink for load failures (message + optional exception).</param>
    public PluginLoader(bool allowLooseSource = true, Action<string, Exception?>? log = null)
    {
        _allowLooseSource = allowLooseSource;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IPlugin>> LoadAllAsync(string pluginsDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);

        var loaded = new List<IPlugin>();
        if (!Directory.Exists(pluginsDirectory))
            return loaded;

        // Compiled plugins: top-level *.dll files.
        foreach (var dll in Directory.EnumerateFiles(pluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            try
            {
                loaded.AddRange(LoadCompiled(dll));
            }
            catch (Exception ex)
            {
                _log?.Invoke($"plugin: failed to load assembly '{dll}'", ex);
            }
        }

        // Compiled plugins with their own dependencies: plugins/<name>/<name>.dll (and no plugin.json).
        foreach (var dir in Directory.EnumerateDirectories(pluginsDirectory))
        {
            var dll = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
            if (File.Exists(Path.Combine(dir, "plugin.json")) || !File.Exists(dll))
                continue;
            try
            {
                loaded.AddRange(LoadCompiled(dll));
            }
            catch (Exception ex)
            {
                _log?.Invoke($"plugin: failed to load assembly '{dll}'", ex);
            }
        }

        // Loose-source plugins: immediate subdirectories carrying a plugin.json.
        if (_allowLooseSource)
        {
            foreach (var dir in Directory.EnumerateDirectories(pluginsDirectory))
            {
                var manifestPath = Path.Combine(dir, "plugin.json");
                if (!File.Exists(manifestPath))
                    continue;

                try
                {
                    var plugin = await LoadLooseAsync(dir, manifestPath, cancellationToken).ConfigureAwait(false);
                    if (plugin is not null)
                        loaded.Add(plugin);
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"plugin: failed to compile loose source in '{dir}'", ex);
                }
            }
        }

        return loaded;
    }

    /// <inheritdoc/>
    public Task UnloadAsync(IPlugin plugin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        if (_contexts.TryRemove(plugin, out var context))
            context.Unload();   // best-effort; the ALC is collected once no plugin objects remain rooted

        return Task.CompletedTask;
    }

    private List<IPlugin> LoadCompiled(string dllPath)
    {
        var context = new PluginLoadContext("plugin:" + Path.GetFileNameWithoutExtension(dllPath), dllPath);

        // Load from a stream so the .dll file itself is not locked (allows replace/hot-reload).
        Assembly assembly;
        using (var stream = File.OpenRead(dllPath))
            assembly = context.LoadFromStream(stream);

        var plugins = InstantiatePlugins(assembly, entryPoint: null).ToList();
        if (plugins.Count == 0)
        {
            context.Unload();
            _log?.Invoke($"plugin: no IPlugin implementation found in '{dllPath}'", null);
            return [];
        }

        foreach (var plugin in plugins)
            _contexts[plugin] = context;

        return plugins;
    }

    private async Task<IPlugin?> LoadLooseAsync(string directory, string manifestPath, CancellationToken cancellationToken)
    {
        var manifest = JsonSerializer.Deserialize<LooseManifest>(
            await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false), ManifestJson);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
            throw new InvalidOperationException("plugin.json is missing or has no 'id'.");

        var sources = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).ToList();
        if (sources.Count == 0)
            return null;

        var trees = new List<SyntaxTree>(sources.Count);
        foreach (var file in sources)
        {
            var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            trees.Add(CSharpSyntaxTree.ParseText(text, path: file, cancellationToken: cancellationToken));
        }

        var assemblyName = "TomYumPlugin_" + Sanitize(manifest.Id);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            BuildReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Enable));

        using var peStream = new MemoryStream();
        var emit = compilation.Emit(peStream, cancellationToken: cancellationToken);
        if (!emit.Success)
        {
            var errors = string.Join(
                "; ",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(5).Select(d => d.ToString()));
            throw new InvalidOperationException($"Roslyn compilation failed: {errors}");
        }

        peStream.Position = 0;
        var context = new PluginLoadContext("plugin:" + assemblyName, mainAssemblyPath: null);
        var assembly = context.LoadFromStream(peStream);

        var plugin = InstantiatePlugins(assembly, manifest.EntryPoint).FirstOrDefault();
        if (plugin is null)
        {
            context.Unload();
            _log?.Invoke($"plugin: loose source in '{directory}' produced no IPlugin (entryPoint '{manifest.EntryPoint}')", null);
            return null;
        }

        _contexts[plugin] = context;
        return plugin;
    }

    private static IEnumerable<IPlugin> InstantiatePlugins(Assembly assembly, string? entryPoint)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface || !typeof(IPlugin).IsAssignableFrom(type))
                continue;

            if (entryPoint is not null
                && !string.Equals(type.Name, entryPoint, StringComparison.Ordinal)
                && !string.Equals(type.FullName, entryPoint, StringComparison.Ordinal))
                continue;

            if (type.GetConstructor(Type.EmptyTypes) is null)
                continue;

            if (Activator.CreateInstance(type) is IPlugin plugin)
                yield return plugin;
        }
    }

    private static List<MetadataReference> BuildReferences()
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Reference every assembly the host runs with (framework + app deps incl. Abstractions/Domain).
        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!File.Exists(path) || !seen.Add(Path.GetFileName(path)))
                continue;
            references.Add(MetadataReference.CreateFromFile(path));
        }

        // Be explicit that the plugin contract assembly is referenceable, even if hosting changed TPA.
        var abstractions = typeof(IPlugin).Assembly.Location;
        if (!string.IsNullOrEmpty(abstractions) && seen.Add(Path.GetFileName(abstractions)))
            references.Add(MetadataReference.CreateFromFile(abstractions));

        return references;
    }

    private static string Sanitize(string id)
    {
        var chars = id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars);
    }

    private sealed record LooseManifest(string Id, string? Name, string? Version, string? Authors, string? Description, string? EntryPoint);
}
