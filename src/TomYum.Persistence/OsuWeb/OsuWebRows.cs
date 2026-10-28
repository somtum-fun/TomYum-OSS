namespace TomYum.Persistence.OsuWeb;

// Row shapes for the osu-web tables TomYum reads and writes. Only the columns TomYum needs, plus
// the NOT NULL columns without a database default (an insert must supply them). Column names and
// types follow osu-web's migrations (base_tables + later alters); osu-web owns this schema, so
// TomYum never creates or alters these tables.

/// <summary>A row of osu-web's <c>phpbb_users</c>.</summary>
public sealed class OsuWebUser
{
    public uint UserId { get; set; }

    /// <summary>1 = banned (osu-web <c>User::isBanned</c>).</summary>
    public sbyte UserType { get; set; }

    /// <summary>The user's default (display) group.</summary>
    public uint GroupId { get; set; }

    public string? UserPermissions { get; set; }

    public string UserIp { get; set; } = string.Empty;

    /// <summary>Unix seconds.</summary>
    public uint UserRegdate { get; set; }

    public string Username { get; set; } = string.Empty;

    /// <summary><c>strtolower(username)</c> — spaces kept, unlike TomYum's safe name.</summary>
    public string UsernameClean { get; set; } = string.Empty;

    /// <summary><c>bcrypt(md5_hex(password))</c>, same as TomYum.</summary>
    public string UserPassword { get; set; } = string.Empty;

    /// <summary>Unix seconds of the last password change.</summary>
    public uint UserPasschg { get; set; }

    public string? UserEmail { get; set; }

    /// <summary>Unix seconds.</summary>
    public uint UserLastvisit { get; set; }

    /// <summary>Non-zero = restricted (osu-web <c>User::isRestricted</c>).</summary>
    public bool UserWarnings { get; set; }

    public string UserSig { get; set; } = string.Empty;

    public string UserOcc { get; set; } = string.Empty;

    public string UserInterests { get; set; } = string.Empty;

    public string UserColour { get; set; } = string.Empty;

    public uint UserRank { get; set; }

    public bool OsuSubscriber { get; set; }

    /// <summary>Upper-case ISO code (osu-web convention); TomYum uses lower-case.</summary>
    public string CountryAcronym { get; set; } = string.Empty;
}

/// <summary>A row of osu-web's <c>phpbb_groups</c> (only what TomYum needs).</summary>
public sealed class OsuWebGroup
{
    public uint GroupId { get; set; }

    /// <summary>Stable name such as <c>default</c>, <c>admin</c>, <c>bot</c> (osu-web <c>Group::PRIV_IDENTIFIERS</c>).</summary>
    public string? Identifier { get; set; }

    public string GroupColour { get; set; } = string.Empty;

    public uint GroupRank { get; set; }
}

/// <summary>A row of osu-web's <c>phpbb_user_group</c> (group membership).</summary>
public sealed class OsuWebUserGroup
{
    public uint GroupId { get; set; }

    public uint UserId { get; set; }

    public bool GroupLeader { get; set; }

    /// <summary>osu-web writes <c>0</c> when adding a member; it doesn't filter on it when reading.</summary>
    public bool UserPending { get; set; }
}

/// <summary>A row of osu-web's <c>osu_beatmapsets</c> (only what TomYum writes; the rest have defaults).</summary>
public sealed class OsuWebBeatmapset
{
    public uint BeatmapsetId { get; set; }

    public string Artist { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Creator { get; set; } = string.Empty;

    /// <summary>osu! API state: -2 graveyard, -1 WIP, 0 pending, 1 ranked, 2 approved, 3 qualified, 4 loved.</summary>
    public sbyte Approved { get; set; }

    public DateTime? ApprovedDate { get; set; }

    /// <summary>
    /// When the set entered the qualified queue: osu-web's <c>modding:rank</c> ranks it once the
    /// qualified period has passed since then. Null while not qualified.
    /// </summary>
    public DateTime? QueuedAt { get; set; }

    public DateTime? SubmitDate { get; set; }

    public DateTime LastUpdate { get; set; }

    /// <summary>
    /// The site only shows a set's cover when this is set (it's the cover URL's cache-buster;
    /// null renders as <c>?0</c>, meaning "no cover").
    /// </summary>
    public DateTime? CoverUpdatedAt { get; set; }

    public float Bpm { get; set; }

    public ushort GenreId { get; set; } = 1;

    public ushort LanguageId { get; set; } = 1;

    public string Source { get; set; } = string.Empty;

    public string Tags { get; set; } = string.Empty;

    public bool Video { get; set; }

    public bool Storyboard { get; set; }
}

/// <summary>
/// A row of osu-web's <c>osu_user_banhistory</c> (read only here): notes, restrictions and silences.
/// osu-web's <c>User::isSilenced</c>: the newest restriction/silence row with a period that hasn't
/// run out. Lifting a silence adds a silence row with period 0.
/// </summary>
public sealed class OsuWebAccountHistory
{
    public const sbyte Note = 0;
    public const sbyte Restriction = 1;
    public const sbyte Silence = 2;

    public uint BanId { get; set; }

    /// <summary>Shown on the player's account history page.</summary>
    public string? Reason { get; set; }

    /// <summary>The staff member who did it.</summary>
    public uint? BannerId { get; set; }

    public bool Permanent { get; set; }

    public uint? UserId { get; set; }

    /// <summary>0 note, 1 restriction, 2 silence, 3 tournament ban.</summary>
    public sbyte? BanStatus { get; set; }

    /// <summary>Length in seconds (0 = none / lifted).</summary>
    public uint Period { get; set; }

    public DateTime? Timestamp { get; set; }
}

/// <summary>A row of osu-web's <c>osu_beatmappacks</c> (a beatmap pack, identified by its tag).</summary>
public sealed class OsuWebBeatmapPack
{
    public short PackId { get; set; }

    public string Url { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    /// <summary>E.g. <c>S788</c>, <c>P304</c> (varchar(5), unique).</summary>
    public string Tag { get; set; } = string.Empty;

    public DateTime Date { get; set; }

    public bool Hidden { get; set; }
}

/// <summary>A row of osu-web's <c>osu_beatmappacks_items</c>: a set in a pack.</summary>
public sealed class OsuWebBeatmapPackItem
{
    public uint ItemId { get; set; }

    public ushort PackId { get; set; }

    public uint BeatmapsetId { get; set; }
}

/// <summary>A row of osu-web's <c>osu_beatmaps</c> (only what TomYum writes; the rest have defaults).</summary>
public sealed class OsuWebBeatmap
{
    public uint BeatmapId { get; set; }

    public uint? BeatmapsetId { get; set; }

    /// <summary>MD5 of the .osu file.</summary>
    public string? Checksum { get; set; }

    /// <summary>The difficulty's file name (see <c>OsuFileName</c>).</summary>
    public string? Filename { get; set; }

    public string Version { get; set; } = string.Empty;

    public uint TotalLength { get; set; }

    public uint HitLength { get; set; }

    public byte Playmode { get; set; }

    public sbyte Approved { get; set; }

    public DateTime LastUpdate { get; set; }

    public float Difficultyrating { get; set; }

    public uint MaxCombo { get; set; }

    public float DiffSize { get; set; }

    public float DiffApproach { get; set; }

    public float DiffOverall { get; set; }

    public float DiffDrain { get; set; }

    /// <summary>osu-web's column default is 60.</summary>
    public float Bpm { get; set; } = 60;

    public uint CountNormal { get; set; }

    public uint CountSlider { get; set; }

    public uint CountSpinner { get; set; }

    public uint CountTotal { get; set; }
}

/// <summary>
/// A row of osu-web's <c>scores</c> table (formerly <c>solo_scores</c>). Stable scores are "legacy"
/// scores: <c>legacy_score_id</c> non-null, the stable score in <c>legacy_total_score</c>.
/// </summary>
public sealed class OsuWebScore
{
    public ulong Id { get; set; }

    public uint UserId { get; set; }

    public ushort RulesetId { get; set; }

    public uint BeatmapId { get; set; }

    public bool HasReplay { get; set; }

    /// <summary>Kept long-term (osu-web: <c>preserve = passed</c>).</summary>
    public bool Preserve { get; set; }

    /// <summary>Counts for leaderboards/pp (osu-web: passed on a map with <c>approved &gt; 0</c>).</summary>
    public bool Ranked { get; set; }

    /// <summary>Grade: XH, X, SH, S, A, B, C, D (F for fails).</summary>
    public string Rank { get; set; } = string.Empty;

    public bool Passed { get; set; }

    /// <summary>0–1.</summary>
    public float Accuracy { get; set; }

    public uint MaxCombo { get; set; }

    public uint TotalScore { get; set; }

    /// <summary>JSON: mods, statistics, maximum_statistics.</summary>
    public string Data { get; set; } = "{}";

    public float? Pp { get; set; }

    public ulong? LegacyScoreId { get; set; }

    public uint LegacyTotalScore { get; set; }

    /// <summary>When a lazer play started (null for stable's): ppy's processor measures a failed lazer play's length from it.</summary>
    public DateTime? StartedAt { get; set; }

    public DateTime EndedAt { get; set; }
}
