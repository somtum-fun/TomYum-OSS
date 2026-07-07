using System.Reflection;

namespace TomYum.Server.Bancho;

/// <summary>
/// Exposes the running TomYum build version. Reads the assembly's informational version
/// (set via <c>VersionPrefix</c>/<c>VersionSuffix</c> in <c>Directory.Build.props</c>, e.g.
/// <c>0.0.1-dev</c>) once at startup and surfaces it to the rest of the server — the startup
/// log, the <c>GET /</c> status page, the health probe, and BanchoBot.
/// </summary>
public sealed class TomYumVersion
{
    /// <summary>Fallback used when no informational version is baked into the assembly.</summary>
    public const string Fallback = "0.1.0-dev";

    /// <summary>Creates the version accessor, resolving the version from the entry assembly.</summary>
    public TomYumVersion()
    {
        Display = Resolve();
    }

    /// <summary>The display version string, e.g. <c>0.0.1-dev</c>.</summary>
    public string Display { get; }

    private static string Resolve()
    {
        var informational = (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly())
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return Fallback;
        }

        // Strip the source-revision suffix the SDK appends (e.g. "0.0.1-dev+abc123").
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? informational[..plus] : informational;
    }
}
