namespace TomYum.Server.Bancho;

/// <summary>
/// Builds the host-header patterns the osu!stable client uses, from the configured public
/// domain. Stable splits its surface across subdomains: <c>c.</c>/<c>c1.</c>–<c>c6.</c>/<c>ce.</c>/
/// <c>cho.</c> carry the bancho binary protocol, while <c>osu.</c> serves the <c>/web/*</c>
/// surface and in-game registration. See <c>memories/protocols/bancho-stable.md</c> §1.
/// </summary>
/// <remarks>
/// Patterns are consumed by <c>RequireHost</c>. A pattern without a port matches any port,
/// so the same values work behind Caddy (:443) and for local <c>dotnet run</c> (:5099).
/// <c>localhost</c> / <c>127.0.0.1</c> are always included so the server is reachable in
/// development without DNS or a hosts-file entry.
/// </remarks>
public static class BanchoHosts
{
    /// <summary>The config key holding the public domain (e.g. <c>tomyum.example</c>).</summary>
    public const string DomainConfigKey = "Server:Domain";

    /// <summary>Domain used when none is configured.</summary>
    public const string DefaultDomain = "localhost";

    /// <summary>Bancho subdomain labels (binary protocol hosts).</summary>
    private static readonly string[] BanchoLabels = ["c", "c1", "c2", "c3", "c4", "c5", "c6", "ce", "cho"];

    /// <summary>Host patterns that serve the bancho binary protocol for <paramref name="domain"/>.</summary>
    public static string[] Bancho(string domain) =>
        [.. BanchoLabels.Select(label => $"{label}.{domain}"), "localhost", "127.0.0.1"];

    /// <summary>Host patterns that serve the <c>/web/*</c> surface and registration for <paramref name="domain"/>.</summary>
    public static string[] Web(string domain) =>
        [$"osu.{domain}", "localhost", "127.0.0.1"];

    /// <summary>Host patterns that serve beatmap assets (thumbnails) for <paramref name="domain"/>.</summary>
    public static string[] Beatmap(string domain) =>
        [$"b.{domain}", "localhost", "127.0.0.1"];

    /// <summary>
    /// Host patterns that serve static assets (medal icons, etc.) for <paramref name="domain"/>. The
    /// devserver-patched client rewrites <c>assets.ppy.sh</c> to <c>assets.&lt;domain&gt;</c>, so medal
    /// popups fetch their icons from here.
    /// </summary>
    public static string[] Assets(string domain) =>
        [$"assets.{domain}", "localhost", "127.0.0.1"];

    /// <summary>osu!'s old static host, <c>s.&lt;domain&gt;</c>: old clients' medal images (docs/old-clients.md).</summary>
    public static string[] Static(string domain) =>
        [$"s.{domain}"];

    /// <summary>Reads the public domain from configuration, falling back to <see cref="DefaultDomain"/>.</summary>
    public static string ResolveDomain(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var domain = configuration[DomainConfigKey];
        return string.IsNullOrWhiteSpace(domain) ? DefaultDomain : domain;
    }
}
