namespace TomYum.Server.Bancho;

/// <summary>
/// osu!stable device verification, as osu.ppy.sh does it (config section <c>ClientVerification</c>;
/// needs the website, osu-web). Logging in from a new device is refused ("verification required")
/// and the client opens the website, where the player signs in and confirms the device; an account's
/// first device is trusted. See <c>OsuWebClientVerification</c>.
/// </summary>
public sealed class ClientVerificationOptions
{
    public const string Section = "ClientVerification";

    /// <summary>Whether new devices must be confirmed on the website. Default: off.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// What happens to a build that can't be asked to verify a device (before b20151228 and not patched
    /// for it, see docs/old-clients.md) when its device would need verifying
    /// (<c>TOMYUM_CLIENTVERIFICATION__OLDCLIENTS</c>). Default: <see cref="OldClientPolicy.Unverified"/>.
    /// </summary>
    public OldClientPolicy OldClients { get; set; } = OldClientPolicy.Unverified;

    /// <summary>
    /// Where a refused old client is sent for a client that can verify (<c>TOMYUM_CLIENTVERIFICATION__DOWNLOADSURL</c>).
    /// Default: the website's download page, <c>https://osu.&lt;domain&gt;/home/download</c>.
    /// </summary>
    public string? DownloadsUrl { get; set; }
}

/// <summary>What happens to an old client that can't verify a new device (<see cref="ClientVerificationOptions.OldClients"/>).</summary>
public enum OldClientPolicy
{
    /// <summary>It logs in without the check (the device stays unverified for newer clients).</summary>
    Unverified,

    /// <summary>
    /// It's refused, with a message linking <see cref="ClientVerificationOptions.DownloadsUrl"/>, but only when
    /// the device actually needs verifying: a verified device, or an account's first, still gets in.
    /// </summary>
    Refuse,
}
