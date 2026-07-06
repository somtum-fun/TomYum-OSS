namespace TomYum.Server.Beatmaps;

/// <summary>
/// Configuration for the osu!Direct beatmap browser endpoints. The mirror must expose a
/// mino.pw-compatible JSON search API (the de-facto standard for private osu! servers;
/// supported by catboy.best, nerinyan.moe, and others).
/// </summary>
public sealed class DirectOptions
{
    public const string Section = "Direct";

    /// <summary>
    /// Base URL of the beatmap mirror (no trailing slash).
    /// Search and download paths are appended to this.
    /// Default: <c>https://catboy.best</c>.
    /// </summary>
    public string MirrorBaseUrl { get; set; } = "https://catboy.best";

    /// <summary>
    /// Path appended to <see cref="MirrorBaseUrl"/> for search requests.
    /// Default: <c>/api/search</c> (catboy.best / mino.pw-compatible).
    /// </summary>
    public string SearchPath { get; set; } = "/api/search";

    /// <summary>
    /// Path template for a single beatmap set lookup by set id.
    /// <c>{0}</c> is replaced with the set id.
    /// Default: <c>/api/s/{0}</c>.
    /// </summary>
    public string SetPath { get; set; } = "/api/s/{0}";

    /// <summary>
    /// Path template for .osz downloads redirected to the mirror.
    /// <c>{0}</c> is replaced with the set id.
    /// Default: <c>/d/{0}</c>.
    /// </summary>
    public string DownloadPath { get; set; } = "/d/{0}";
}
