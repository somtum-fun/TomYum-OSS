namespace TomYum.Server.OsuWeb;

/// <summary>
/// osu-web's InterOp API (<c>/_lio/*</c>), bound from the <c>OsuWeb</c> section. Both values are
/// needed; without them medals are written straight to osu-web's tables instead (no activity-feed
/// entry) and in-game sign-ups aren't pushed to the website search.
/// </summary>
public sealed class OsuWebInteropOptions
{
    public const string SectionName = "OsuWeb";

    /// <summary>
    /// Where TomYum reaches osu-web, e.g. <c>http://nginx</c> inside osu-web's compose network.
    /// osu-web verifies the signature against the URL it sees, so use a URL whose host it receives
    /// unchanged (no port rewriting proxy in between).
    /// </summary>
    public string? InteropUrl { get; set; }

    /// <summary>osu-web's <c>SHARED_INTEROP_SECRET</c> (a secret: set via <c>TOMYUM_OSUWEB__INTEROPSECRET</c>).</summary>
    public string? InteropSecret { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(InteropUrl) && !string.IsNullOrWhiteSpace(InteropSecret);
}
