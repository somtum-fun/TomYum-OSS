namespace TomYum.Tools.ImportBanchoPy;

/// <summary>Everything one run of the importer was told.</summary>
internal sealed class ImportSettings
{
    /// <summary>bancho.py's database on the same server (the restored dump), e.g. <c>bpy_import</c>.</summary>
    public required string Source { get; init; }

    /// <summary>TomYum's database.</summary>
    public required string TomYum { get; init; }

    /// <summary>osu-web's database.</summary>
    public required string OsuWeb { get; init; }

    /// <summary>bancho.py's <c>.data</c> folder (<c>osr/</c> replays, <c>osz/</c> private sets), or null.</summary>
    public string? DataPath { get; init; }

    /// <summary>Where the run writes its files (log, csv).</summary>
    public required string OutDir { get; init; }

    /// <summary>Where TomYum keeps replays (<c>Replays:Path</c>).</summary>
    public required string ReplaysPath { get; init; }

    /// <summary>Where TomYum caches <c>.osu</c> files (<c>Performance:OsuCacheDirectory</c>, default <c>data/osu-cache</c>).</summary>
    public required string OsuCachePath { get; init; }

    /// <summary>TomYum's BanchoBot id (<c>Bancho:BotUserId</c>).</summary>
    public int BotUserId { get; init; } = 3;

    /// <summary>bancho.py's bot id (1 on every bancho.py). Its rows are written as BanchoBot's; it gets no account.</summary>
    public int OldBotId { get; init; } = 1;

    /// <summary>Old id -> new id for players who can't keep their id (one holds BanchoBot's).</summary>
    public IReadOnlyDictionary<int, int> Remaps { get; init; } = new Dictionary<int, int>();

    /// <summary>Leave out restricted accounts that never played, logged in or sent a device hash.</summary>
    public bool SkipEmptyRestricted { get; init; }

    /// <summary>Go ahead although the target already has players or scores.</summary>
    public bool AllowExisting { get; init; }

    /// <summary>Also queue the website copies to ppy's score processor (when the server uses it).</summary>
    public bool ScoreProcessor { get; init; }

    /// <summary>TomYum's address for the custom-set uploads (the server must be running), e.g. <c>http://server:8080</c>.</summary>
    public Uri? ServerUrl { get; init; }

    /// <summary>The server's domain: uploads go to host <c>osu.&lt;domain&gt;</c>.</summary>
    public string? Domain { get; init; }

    /// <summary>The InterOp secret that signs upload tickets (<c>OsuWeb:InteropSecret</c>).</summary>
    public string? InteropSecret { get; init; }

    /// <summary>Redis, for the website's queues (<c>Redis:Configuration</c>).</summary>
    public string RedisConfiguration { get; init; } = "localhost:6379";

    /// <summary>The website's score index prefix (<c>Database:OsuWebIndexPrefix</c>).</summary>
    public string IndexPrefix { get; init; } = string.Empty;
}

/// <summary>One line of the plan: a count with a label, and sometimes a warning.</summary>
internal sealed record PlanLine(string Label, long Count, string? Note = null);
