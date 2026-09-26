using System.Text.Json.Serialization;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// One element of the osu! v1 <c>get_beatmaps</c> JSON response. Only the fields TomYum needs to
/// populate a <c>Beatmap</c> are mapped here; field names match the API exactly (see bancho.py
/// <c>_parse_from_osuapi_resp</c>).
/// </summary>
/// <remarks>
/// The two API flavours disagree on numeric encoding: <c>old.ppy.sh</c> returns every field as a
/// quoted string, while the keyless <c>osu.direct</c> mirror returns ids/lengths/ratings as bare
/// JSON numbers. <see cref="OsuApiClient"/> deserialises with a <see cref="FlexibleStringConverter"/>
/// so both shapes bind to these <c>string?</c> properties; values are then parsed defensively when
/// projecting onto the domain <c>Beatmap</c>. Unmapped fields (tags, favourite_count, etc.) are ignored.
/// </remarks>
public sealed class OsuApiBeatmap
{
    /// <summary>MD5 of the <c>.osu</c> file.</summary>
    [JsonPropertyName("file_md5")]
    public string? FileMd5 { get; set; }

    /// <summary>Owning beatmap set id.</summary>
    [JsonPropertyName("beatmapset_id")]
    public string? BeatmapsetId { get; set; }

    /// <summary>Beatmap (difficulty) id.</summary>
    [JsonPropertyName("beatmap_id")]
    public string? BeatmapId { get; set; }

    /// <summary>Track artist.</summary>
    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    /// <summary>Track title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Difficulty (version) name.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>Mapper username.</summary>
    [JsonPropertyName("creator")]
    public string? Creator { get; set; }

    /// <summary>Last upstream update, as <c>yyyy-MM-dd HH:mm:ss</c>.</summary>
    [JsonPropertyName("last_update")]
    public string? LastUpdate { get; set; }

    /// <summary>Total length in seconds.</summary>
    [JsonPropertyName("total_length")]
    public string? TotalLength { get; set; }

    /// <summary>Maximum achievable combo (nullable).</summary>
    [JsonPropertyName("max_combo")]
    public string? MaxCombo { get; set; }

    /// <summary>osu! v1 approval status integer (mapped via <c>RankedStatus.FromOsuApi</c>).</summary>
    [JsonPropertyName("approved")]
    public string? Approved { get; set; }

    /// <summary>Game mode integer.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>Star difficulty rating.</summary>
    [JsonPropertyName("difficultyrating")]
    public string? DifficultyRating { get; set; }
}
