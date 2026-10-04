namespace TomYum.Gateway;

/// <summary>The gateway's settings (section <c>Gateway</c>; env <c>TOMYUM_GATEWAY__*</c>).</summary>
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>The main server's domain (e.g. <c>tomyum.example</c>): bancho is <c>c.&lt;domain&gt;</c>.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// Where the main server is: <c>https://c.&lt;domain&gt;</c> by default (through Cloudflare, for a
    /// gateway on another box), or e.g. <c>http://server:8080</c> next to it. Requests still carry
    /// <c>c.&lt;domain&gt;</c> as their host.
    /// </summary>
    public string? Upstream { get; set; }

    /// <summary>The secret shared with the main server (its <c>Gateway:Key</c>).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Connections allowed at once from one address (bancho and IRC each).</summary>
    public int MaxConnectionsPerIp { get; set; } = 8;

    public BanchoOptions Bancho { get; set; } = new();

    public IrcOptions Irc { get; set; } = new();

    public Uri UpstreamUri => new(string.IsNullOrWhiteSpace(Upstream) ? $"https://c.{Domain}/" : Upstream.TrimEnd('/') + "/");

    public string UpstreamHost => $"c.{Domain}";

    /// <summary>pre-2014 builds' bancho over TCP (<c>server.&lt;domain&gt;</c>), run by the main server's TCP bancho.</summary>
    public sealed class BanchoOptions
    {
        public bool Enabled { get; set; } = true;

        /// <summary>The ports to listen on (osu!'s was 13381).</summary>
        public int[] Ports { get; set; } = [13381];
    }

    /// <summary>IRC (<c>irc.&lt;domain&gt;</c>), relayed to the main server over a WebSocket.</summary>
    public sealed class IrcOptions
    {
        public bool Enabled { get; set; }

        public int Port { get; set; } = 6667;
    }
}
