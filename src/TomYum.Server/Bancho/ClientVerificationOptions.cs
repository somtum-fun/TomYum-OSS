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
}
