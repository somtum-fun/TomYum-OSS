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
/// CLR types the host sees — never a duplicate that would fail to cast.
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

        var path = _resolver?.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
