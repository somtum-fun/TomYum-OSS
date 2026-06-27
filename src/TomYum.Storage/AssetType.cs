namespace TomYum.Storage;

/// <summary>
/// Logical categories of assets the server stores. Storage routing and
/// per-asset-type provider selection are keyed on this enum.
/// </summary>
public enum AssetType
{
    /// <summary>User avatar images.</summary>
    Avatar,

    /// <summary>In-game screenshots uploaded via <c>osu-screenshot.php</c>.</summary>
    Screenshot,

    /// <summary>Replay files (<c>.osr</c> / raw LZMA frame blobs).</summary>
    Replay,

    /// <summary>Individual beatmap files (<c>.osu</c>).</summary>
    Beatmap,

    /// <summary>Beatmap set archives (<c>.osz</c>).</summary>
    BeatmapSet,

    /// <summary>Beatmap background / preview audio and other set media.</summary>
    BeatmapMedia,
}
