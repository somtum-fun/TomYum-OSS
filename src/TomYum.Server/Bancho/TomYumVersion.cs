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

    /// <summary>Used for <see cref="Commit"/> when no git revision is baked into the assembly.</summary>
    public const string UnknownCommit = "unknown";

    /// <summary>The software's name, and the server's when it has none of its own.</summary>
    public const string Product = "TomYum";

    /// <summary>Creates the version accessor, resolving the version from the entry assembly.</summary>
    public TomYumVersion()
        : this(serverName: null)
    {
    }

    /// <summary>With the server's own name from configuration (<c>Server:Name</c>, <c>TOMYUM_SERVER__NAME</c>).</summary>
    public TomYumVersion(IConfiguration configuration)
        : this(configuration["Server:Name"])
    {
    }

    private TomYumVersion(string? serverName)
    {
        var informational = (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly())
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        (Display, Commit) = ParseInformationalVersion(informational);
        ServerName = string.IsNullOrWhiteSpace(serverName) ? Product : serverName.Trim();
    }

    /// <summary>The display version string, e.g. <c>0.0.1-dev</c>.</summary>
    public string Display { get; }

    /// <summary>What players know the server as (e.g. <c>example</c>); <see cref="Product"/> by default.</summary>
    public string ServerName { get; }

    /// <summary>
    /// The server and the build, for the bot's posts and <c>!version</c>: <c>example · TomYum 0.3.0-dev</c>,
    /// or <c>TomYum 0.3.0-dev</c> for a server without a name of its own.
    /// </summary>
    public string Label => ServerName == Product ? $"{Product} {Display}" : $"{ServerName} · {Product} {Display}";

    /// <summary>
    /// The short git commit the build was produced from (baked in via <c>SourceRevisionId</c>; see
    /// <c>Directory.Build.props</c>), or <see cref="UnknownCommit"/> when no revision was baked in.
    /// </summary>
    public string Commit { get; }

    /// <summary>
    /// Splits the assembly informational version (<c>"&lt;version&gt;+&lt;commit&gt;"</c>) into the display
    /// version and the source-revision suffix the SDK appends. Both fall back when absent/empty.
    /// </summary>
    internal static (string Display, string Commit) ParseInformationalVersion(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            return (Fallback, UnknownCommit);
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return (informational, UnknownCommit);
        }

        var display = informational[..plus];
        var commit = informational[(plus + 1)..];
        return (display.Length == 0 ? Fallback : display,
                commit.Length == 0 ? UnknownCommit : commit);
    }
}
