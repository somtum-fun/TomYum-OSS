namespace TomYum.Server.Anticheat;

/// <summary>
/// Server-side configuration for the anti-cheat subsystem, bound from the <c>Anticheat</c>
/// section of <c>appsettings</c>/environment. These settings are the ones the host owns; per-plugin
/// anti-cheat knobs stay under each plugin's own <c>plugins:&lt;id&gt;</c> config subtree.
/// </summary>
public sealed class AnticheatOptions
{
    /// <summary>Configuration section name (<c>Anticheat</c>).</summary>
    public const string SectionName = "Anticheat";

    /// <summary>
    /// When true, every vetoable AC event is skipped and
    /// <see cref="TomYum.Plugins.Abstractions.IChallengeService.Verify"/> accepts every payload
    /// without checking the signature. The global AC kill switch for development; per-plugin
    /// dev-mode knobs stay plugin-local.
    /// </summary>
    public bool DevMode { get; set; }

    /// <summary>How long an issued challenge is valid for. Default 10 s.</summary>
    public TimeSpan HandshakeGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// HMAC-SHA256 shared secret for handshake flag verification. Empty + <see cref="DevMode"/>
    /// false makes every verify fail with <c>no_secret_configured</c>, so a misconfigured
    /// production server fails closed rather than letting every client in.
    /// </summary>
    /// <remarks>
    /// DEPRECATED fallback. Will be removed in a future release once all AC clients negotiate
    /// per-session HMAC keys via <c>POST /anticheat/key</c>
    /// (see <see cref="TomYum.Plugins.Abstractions.IChallengeService.IssueSessionKey"/>).
    /// Leave empty on new deployments to force per-session keys only — Verify will then reject
    /// any client that still ships the long-lived compile-time secret with
    /// <c>no_secret_configured</c>.
    /// </remarks>
    public string HandshakeSharedSecret { get; set; } = string.Empty;
}
