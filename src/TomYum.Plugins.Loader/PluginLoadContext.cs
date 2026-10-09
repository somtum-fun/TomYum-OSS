using System.Reflection;
using System.Runtime.Loader;

namespace TomYum.Plugins.Loader;

/// <summary>
/// Collectible <see cref="AssemblyLoadContext"/> for a single plugin, so the host can unload it
/// (ADR-0007 hot-unload).
/// </summary>
/// <remarks>
/// Plugin-private dependencies co-located with a compiled <c>.dll</c> resolve inside this context
/// via an <see cref="AssemblyDependencyResolver"/>. The shared contract assemblies
/// (<c>TomYum.Plugins.Abstractions</c>, <c>TomYum.Domain</c>) and the framework deliberately fall
/// through to the default context, so a plugin's <c>IPlugin</c>/entity types are the <em>same</em>
/// CLR types the host sees — never a duplicate that would fail to cast. So does any assembly the host
/// ships in its own folder (a compiled plugin built against <c>TomYum.Server</c> brings copies of it).
/// </remarks>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string[] SharedAssemblies =
    [
        "TomYum.Plugins.Abstractions",
        "TomYum.Domain",
    ];

    private readonly AssemblyDependencyResolver? _resolver;

    public PluginLoadContext(string name, string? mainAssemblyPath)
        : base(name, isCollectible: true)
    {
        _resolver = mainAssemblyPath is null ? null : new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Always share the host's copy of the contract + domain assemblies so plugin types unify.
        if (assemblyName.Name is not null && Array.IndexOf(SharedAssemblies, assemblyName.Name) >= 0)
            return null;

        // Whatever the host ships itself (its own assemblies and their NuGet dependencies) comes from the
        // host too, so a plugin built against the host shares its types instead of loading copies.
        if (assemblyName.Name is not null && File.Exists(Path.Combine(AppContext.BaseDirectory, assemblyName.Name + ".dll")))
            return null;

        var path = _resolver?.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
