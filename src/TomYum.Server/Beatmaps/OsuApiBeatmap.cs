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

    // --- Website-only metadata (projected onto BeatmapDetails for the osu-web mirror) ---

    /// <summary>Circle size.</summary>
    [JsonPropertyName("diff_size")]
    public string? DiffSize { get; set; }

    /// <summary>Approach rate.</summary>
    [JsonPropertyName("diff_approach")]
    public string? DiffApproach { get; set; }

    /// <summary>Overall difficulty.</summary>
    [JsonPropertyName("diff_overall")]
    public string? DiffOverall { get; set; }

    /// <summary>HP drain rate.</summary>
    [JsonPropertyName("diff_drain")]
    public string? DiffDrain { get; set; }

    /// <summary>Beats per minute.</summary>
    [JsonPropertyName("bpm")]
    public string? Bpm { get; set; }

    /// <summary>Drain time in seconds.</summary>
    [JsonPropertyName("hit_length")]
    public string? HitLength { get; set; }

    /// <summary>Circle count (old.ppy.sh only).</summary>
    [JsonPropertyName("count_normal")]
    public string? CountNormal { get; set; }

    /// <summary>Slider count (old.ppy.sh only).</summary>
    [JsonPropertyName("count_slider")]
    public string? CountSlider { get; set; }

    /// <summary>Spinner count (old.ppy.sh only).</summary>
    [JsonPropertyName("count_spinner")]
    public string? CountSpinner { get; set; }

    /// <summary>Genre id (osu.direct reports 0).</summary>
    [JsonPropertyName("genre_id")]
    public string? GenreId { get; set; }

    /// <summary>Language id (osu.direct reports 0).</summary>
    [JsonPropertyName("language_id")]
    public string? LanguageId { get; set; }

    /// <summary>Song source.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>Space-separated tags.</summary>
    [JsonPropertyName("tags")]
    public string? Tags { get; set; }

    /// <summary>1 when the set has a video.</summary>
    [JsonPropertyName("video")]
    public string? Video { get; set; }

    /// <summary>1 when the set has a storyboard.</summary>
    [JsonPropertyName("storyboard")]
    public string? Storyboard { get; set; }

    /// <summary>Ranked/approved/loved date.</summary>
    [JsonPropertyName("approved_date")]
    public string? ApprovedDate { get; set; }
}
