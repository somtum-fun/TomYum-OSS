namespace TomYum.Server.Beatmaps;

/// <summary>
/// Configuration for the osu!Direct beatmap browser endpoints. The mirror must expose a
/// mino.pw-compatible JSON search API (the de-facto standard for private osu! servers;
/// supported by osu.direct, catboy.best, nerinyan.moe, and others).
/// </summary>
public sealed class DirectOptions
{
    public const string Section = "Direct";

    /// <summary>
    /// Base URL of the beatmap mirror (no trailing slash).
    /// Default: <c>https://osu.direct</c>.
    /// </summary>
    public string MirrorBaseUrl { get; set; } = "https://osu.direct";

    /// <summary>
    /// Path appended to <see cref="MirrorBaseUrl"/> for search requests.
    /// Default: <c>/api/search</c>.
    /// </summary>
    public string SearchPath { get; set; } = "/api/search";

    /// <summary>
    /// Path template for a single beatmap set lookup by set id.
    /// <c>{0}</c> is replaced with the set id.
    /// Default: <c>/api/s/{0}</c>.
    /// </summary>
    public string SetPath { get; set; } = "/api/s/{0}";

    /// <summary>
    /// Path template for one difficulty by its id (its <c>ParentSetID</c> gives the set).
    /// Default: <c>/api/b/{0}</c>.
    /// </summary>
    public string BeatmapPath { get; set; } = "/api/b/{0}";

    /// <summary>
    /// Path template for one difficulty by its checksum (its <c>ParentSetID</c> gives the set).
    /// Default: <c>/api/md5/{0}</c>.
    /// </summary>
    public string ChecksumPath { get; set; } = "/api/md5/{0}";

    /// <summary>
    /// Path template for .osz downloads redirected to the mirror.
    /// <c>{0}</c> is replaced with the set id.
    /// Default: <c>/api/d/{0}</c>.
    /// </summary>
    public string DownloadPath { get; set; } = "/api/d/{0}";

    /// <summary>
    /// Base URL for beatmap assets (thumbnails, audio previews, etc.) served from ppy's CDN.
    /// Every request to the <c>b.</c> subdomain is redirected to this base with the path
    /// forwarded verbatim — the same strategy as bancho.py's <c>map.py</c> domain.
    /// Default: <c>https://b.ppy.sh</c>.
    /// </summary>
    public string ThumbnailBaseUrl { get; set; } = "https://b.ppy.sh";
}
