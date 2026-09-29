using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>A difficulty's details, as the website's beatmap page shows them.</summary>
public sealed record MapCard(
    int BeatmapId, int BeatmapsetId, string Artist, string Title, string Version, string Creator, int Ruleset, int Status,
    double StarRating, int TotalLength, int HitLength, double Bpm, double CircleSize, double ApproachRate,
    double OverallDifficulty, double DrainRate, int MaxCombo, long PlayCount, long PassCount,
    int ObjectCount = 0, DateTime? RankedAt = null, DateTime? UpdatedAt = null)
{
    public string Name => $"{Artist} - {Title} [{Version}]";
}

/// <summary>A medal of the catalogue, and when the player unlocked it (null: not yet).</summary>
public sealed record MedalProgress(int MedalId, string Name, string Grouping, int Ordering, DateTime? UnlockedAt);

/// <summary>An account as staff see it.</summary>
public sealed record AccountCard(
    int UserId, string Username, string Country, DateTime JoinedAt, DateTime? LastVisit, string RegistrationIp,
    bool Restricted, bool Banned, IReadOnlyList<AccountHistoryEntry> History);

/// <summary>A note, restriction or silence on an account (<c>osu_user_banhistory</c>).</summary>
public sealed record AccountHistoryEntry(string Kind, string Reason, DateTime At, TimeSpan? Length, string? By);

/// <summary>A report from the website (or <c>!report</c>).</summary>
public sealed record ReportCard(
    int ReportId, int ReporterId, string ReporterName, int UserId, string Username, string? Type, ulong? ReportableId,
    string? Reason, string Comments, DateTime At);

/// <summary>Numbers for the status message.</summary>
public sealed record ServerNumbers(long Players, long PlaysToday, long RankedSets, int PeakOnlineToday, long NewPlayersToday);

public sealed partial class OsuWebCommunity
{
    /// <summary>Report reasons the website knows.</summary>
    public static readonly IReadOnlyList<string> ReportReasons =
        ["Cheating", "MultipleAccounts", "Insults", "Spam", "UnwantedContent", "Nonsense", "InappropriateChat", "Other"];

    /// <summary>A difficulty's details, or null when the website doesn't have it.</summary>
    public async Task<MapCard?> GetMapAsync(int beatmapId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Database.SqlQueryRaw<MapRow>(
            """
            SELECT b.beatmap_id AS BeatmapId, b.beatmapset_id AS BeatmapsetId, s.artist AS Artist, s.title AS Title, b.version AS Version,
              s.creator AS Creator, b.playmode AS Ruleset, b.approved AS Status, b.difficultyrating AS StarRating,
              CAST(b.total_length AS SIGNED) AS TotalLength, CAST(b.hit_length AS SIGNED) AS HitLength, b.bpm AS Bpm,
              b.diff_size AS CircleSize, b.diff_approach AS ApproachRate, b.diff_overall AS OverallDifficulty, b.diff_drain AS DrainRate,
              CAST(b.max_combo AS SIGNED) AS MaxCombo, CAST(b.playcount AS SIGNED) AS PlayCount, CAST(b.passcount AS SIGNED) AS PassCount,
              CAST(b.countTotal AS SIGNED) AS ObjectCount, s.approved_date AS RankedAt, s.last_update AS UpdatedAt
            FROM osu_beatmaps b JOIN osu_beatmapsets s ON s.beatmapset_id = b.beatmapset_id
            WHERE b.beatmap_id = {0} AND b.deleted_at IS NULL
            """, beatmapId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows is not [var r])
            return null;
        return new MapCard((int)r.BeatmapId, (int)r.BeatmapsetId, r.Artist, r.Title, r.Version, r.Creator, r.Ruleset, r.Status,
            r.StarRating, (int)r.TotalLength, (int)r.HitLength, r.Bpm, r.CircleSize, r.ApproachRate, r.OverallDifficulty, r.DrainRate,
            (int)r.MaxCombo, r.PlayCount, r.PassCount, (int)r.ObjectCount,
            r.RankedAt is { } ranked ? DateTime.SpecifyKind(ranked, DateTimeKind.Utc) : null,
            r.UpdatedAt is { } updated ? DateTime.SpecifyKind(updated, DateTimeKind.Utc) : null);
    }

    /// <summary>A set's hardest difficulty (for set links), or null.</summary>
    public async Task<int?> GetHardestMapAsync(int beatmapsetId, CancellationToken cancellationToken = default) =>
        await _db.Beatmaps.AsNoTracking()
            .Where(b => b.BeatmapsetId == (uint)beatmapsetId)
            .OrderByDescending(b => b.Difficultyrating)
            .Select(b => (int?)b.BeatmapId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The player's global rank for each of the last 90 days in <paramref name="ruleset"/>, oldest
    /// first (0: unranked that day). Empty when nothing is recorded.
    /// </summary>
    public async Task<IReadOnlyList<int>> GetRankHistoryAsync(int userId, int ruleset, CancellationToken cancellationToken = default)
    {
        var names = new[] { "osu", "taiko", "fruits", "mania" };
        var column = await _db.Database
            .SqlQuery<long>($"SELECT CAST(count AS SIGNED) AS Value FROM osu_counts WHERE name = {"pp_rank_column_" + names[Math.Clamp(ruleset, 0, 3)]}")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (column.Count == 0)
            return [];

        var columns = string.Join(", ", Enumerable.Range(0, 90).Select(i => string.Create(CultureInfo.InvariantCulture, $"r{i}")));
        // EF's own connection (not disposed here): only fixed column names are in the query.
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {columns} FROM osu_user_performance_rank WHERE user_id = @user AND mode = @mode";
            var user = command.CreateParameter();
            user.ParameterName = "@user";
            user.Value = userId;
            command.Parameters.Add(user);
            var mode = command.CreateParameter();
            mode.ParameterName = "@mode";
            mode.Value = ruleset;
            command.Parameters.Add(mode);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return [];

            // The ring's newest day is the recorded column; the day after it is the oldest.
            var newest = (int)column[0];
            var days = new List<int>(90);
            for (var i = 1; i <= 90; i++)
                days.Add(Convert.ToInt32(reader.GetValue((newest + i) % 90), CultureInfo.InvariantCulture));
            return days;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Every enabled medal, with the player's unlock time where they have it.</summary>
    public async Task<IReadOnlyList<MedalProgress>> GetMedalProgressAsync(int userId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Database.SqlQueryRaw<MedalProgressRow>(
            """
            SELECT a.achievement_id AS MedalId, a.name AS Name, a.`grouping` AS `Grouping`, a.ordering AS Ordering, ua.`date` AS UnlockedAt
            FROM osu_achievements a
            LEFT JOIN osu_user_achievements ua ON ua.achievement_id = a.achievement_id AND ua.user_id = {0}
            WHERE a.enabled = 1
            ORDER BY a.`grouping`, a.ordering, a.achievement_id
            """, userId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new MedalProgress((int)r.MedalId, r.Name, r.Grouping, r.Ordering,
            r.UnlockedAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null)).ToList();
    }

    /// <summary>The player's best passed score on a map (by total score, as leaderboards rank), or null.</summary>
    public async Task<ScoreCard?> GetBestOnMapAsync(int userId, int beatmapId, int ruleset, CancellationToken cancellationToken = default) =>
        await QueryScoresAsync(
            "s.user_id = {0} AND s.beatmap_id = {1} AND s.ruleset_id = {2} AND s.passed = 1 AND s.preserve = 1",
            "s.total_score DESC, s.id",
            [userId, beatmapId, ruleset],
            1,
            cancellationToken).ConfigureAwait(false) is [var best, ..] ? best : null;

    /// <summary>
    /// The best pass on a map set in a time window (by pp, then total score) by a visible player, or
    /// null: the map of the day's winner.
    /// </summary>
    public async Task<ScoreCard?> GetBestBetweenAsync(int beatmapId, int ruleset, DateTime from, DateTime to, CancellationToken cancellationToken = default) =>
        await QueryScoresAsync(
            $"s.beatmap_id = {{0}} AND s.ruleset_id = {{1}} AND s.passed = 1 AND s.ended_at >= {{2}} AND s.ended_at < {{3}} AND {Visible}",
            "COALESCE(s.pp, 0) DESC, s.total_score DESC, s.id",
            [beatmapId, ruleset, from, to],
            1,
            cancellationToken).ConfigureAwait(false) is [var best, ..] ? best : null;

    /// <summary>
    /// A random ranked difficulty (ranked or approved, between <paramref name="minStars"/> and
    /// <paramref name="maxStars"/>), or null when there's none.
    /// </summary>
    public async Task<int?> GetRandomRankedMapAsync(double minStars, double maxStars, CancellationToken cancellationToken = default)
    {
        var ids = await _db.Database.SqlQueryRaw<uint>(
            """
            SELECT b.beatmap_id AS Value FROM osu_beatmaps b
            WHERE b.approved IN (1, 2) AND b.deleted_at IS NULL AND b.difficultyrating BETWEEN {0} AND {1} AND b.total_length BETWEEN 60 AND 420
            ORDER BY RAND() LIMIT 1
            """, minStars, maxStars).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ids.Count == 1 ? (int)ids[0] : null;
    }

    /// <summary>The player's best pp before <paramref name="beforeScoreId"/> in <paramref name="ruleset"/>, or 0.</summary>
    public async Task<double> GetBestPpBeforeAsync(int userId, int ruleset, ulong beforeScoreId, CancellationToken cancellationToken = default) =>
        await _db.Scores.AsNoTracking()
            .Where(s => s.UserId == (uint)userId && s.RulesetId == (ushort)ruleset && s.Id < beforeScoreId && s.Passed && s.Pp != null)
            .MaxAsync(s => (double?)s.Pp, cancellationToken).ConfigureAwait(false) ?? 0;

    /// <summary>A website score's stable (TomYum) score id and player, or null when it's a lazer score or unknown.</summary>
    public async Task<(long StableScoreId, int UserId)?> GetStableScoreAsync(ulong websiteScoreId, CancellationToken cancellationToken = default)
    {
        var row = await _db.Scores.AsNoTracking().Where(s => s.Id == websiteScoreId)
            .Select(s => new { s.LegacyScoreId, s.UserId }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row?.LegacyScoreId is { } legacy ? ((long)legacy, (int)row.UserId) : null;
    }

    /// <summary>A difficulty's length in seconds (0 when unknown).</summary>
    public async Task<int> GetMapLengthAsync(int beatmapId, CancellationToken cancellationToken = default) =>
        (int)(await _db.Beatmaps.AsNoTracking().Where(b => b.BeatmapId == (uint)beatmapId)
            .Select(b => (uint?)b.TotalLength).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0);

    /// <summary>An account for staff (restricted and banned ones included), or null.</summary>
    public async Task<AccountCard?> GetAccountAsync(int userId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == (uint)userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return null;

        var history = await _db.Database.SqlQueryRaw<HistoryRow>(
            """
            SELECT CAST(h.ban_status AS SIGNED) AS Status, COALESCE(h.reason, '') AS Reason, h.`timestamp` AS At,
              CAST(h.period AS SIGNED) AS Period, b.username AS BannerName
            FROM osu_user_banhistory h LEFT JOIN phpbb_users b ON b.user_id = h.banner_id
            WHERE h.user_id = {0}
            ORDER BY h.ban_id DESC LIMIT 8
            """, userId).ToListAsync(cancellationToken).ConfigureAwait(false);

        return new AccountCard(
            (int)user.UserId, user.Username, user.CountryAcronym,
            DateTimeOffset.FromUnixTimeSeconds(user.UserRegdate).UtcDateTime,
            user.UserLastvisit > 0 ? DateTimeOffset.FromUnixTimeSeconds(user.UserLastvisit).UtcDateTime : null,
            user.UserIp, user.UserWarnings, user.UserType == 1,
            history.Select(h => new AccountHistoryEntry(
                h.Status switch { 0 => "note", 1 => "restriction", 2 => "silence", _ => "entry" },
                h.Reason,
                DateTime.SpecifyKind(h.At ?? DateTime.UnixEpoch, DateTimeKind.Utc),
                h.Period > 0 ? TimeSpan.FromSeconds(h.Period) : null,
                h.BannerName)).ToList());
    }

    /// <summary>The newest report id (where the report feed starts).</summary>
    public async Task<int> GetLatestReportIdAsync(CancellationToken cancellationToken = default) =>
        (int)(await _db.Database.SqlQueryRaw<uint?>("SELECT MAX(report_id) AS Value FROM osu_user_reports")
            .SingleAsync(cancellationToken).ConfigureAwait(false) ?? 0);

    /// <summary>Reports after <paramref name="afterId"/>, oldest first.</summary>
    public async Task<IReadOnlyList<ReportCard>> GetReportsAfterAsync(int afterId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Database.SqlQueryRaw<ReportRow>(
            """
            SELECT r.report_id AS ReportId, r.reporter_id AS ReporterId, COALESCE(rep.username, '') AS ReporterName,
              r.user_id AS UserId, COALESCE(u.username, '') AS Username, r.reportable_type AS Type, r.reportable_id AS ReportableId,
              r.reason AS Reason, COALESCE(r.comments, '') AS Comments, r.`timestamp` AS At
            FROM osu_user_reports r
            LEFT JOIN phpbb_users rep ON rep.user_id = r.reporter_id
            LEFT JOIN phpbb_users u ON u.user_id = r.user_id
            WHERE r.report_id > {0}
            ORDER BY r.report_id LIMIT 50
            """, afterId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new ReportCard((int)r.ReportId, (int)r.ReporterId, r.ReporterName, (int)r.UserId, r.Username, r.Type,
            r.ReportableId, r.Reason, r.Comments, DateTime.SpecifyKind(r.At, DateTimeKind.Utc))).ToList();
    }

    /// <summary>A report on a player (as the website files one on a user), for <c>!report</c>. Returns its id.</summary>
    public async Task<int> AddReportAsync(int reporterId, int userId, string reason, string comments, CancellationToken cancellationToken = default)
    {
        if (!ReportReasons.Contains(reason))
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not one of the website's report reasons.");
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO osu_user_reports (user_id, reportable_type, reportable_id, reason, reporter_id, comments, `timestamp`)
            VALUES ({userId}, 'user', {userId}, {reason}, {reporterId}, {comments}, UTC_TIMESTAMP())
            """, cancellationToken).ConfigureAwait(false);
        return (int)(await _db.Database.SqlQueryRaw<uint?>("SELECT MAX(report_id) AS Value FROM osu_user_reports WHERE reporter_id = {0}", reporterId)
            .SingleAsync(cancellationToken).ConfigureAwait(false) ?? 0);
    }

    /// <summary>How many reports the player filed in the last <paramref name="window"/> (to stop spam).</summary>
    public async Task<int> CountReportsByAsync(int reporterId, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var since = DateTime.UtcNow - window;
        return (int)await _db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM osu_user_reports WHERE reporter_id = {0} AND `timestamp` >= {1}", reporterId, since)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Numbers for the status message (today = the last 24 hours).</summary>
    public async Task<ServerNumbers> GetServerNumbersAsync(CancellationToken cancellationToken = default)
    {
        var since = DateTime.UtcNow.AddDays(-1);
        var sinceUnix = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
        var players = await _db.Database.SqlQueryRaw<long>($"SELECT COUNT(*) AS Value FROM phpbb_users u WHERE {Visible} AND u.user_id <> 1")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var plays = await _db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM scores WHERE ended_at >= {0}", since)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var ranked = await _db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM osu_beatmapsets WHERE approved > 0 AND deleted_at IS NULL")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var peak = await _db.Database.SqlQueryRaw<long?>("SELECT MAX(users_osu + users_lazer) AS Value FROM osu_banchostats WHERE `date` >= {0}", since)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var joined = await _db.Database.SqlQueryRaw<long>($"SELECT COUNT(*) AS Value FROM phpbb_users u WHERE {Visible} AND u.user_regdate >= {{0}}", sinceUnix)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        return new ServerNumbers(players, plays, ranked, (int)(peak ?? 0), joined);
    }

    private sealed class MapRow
    {
        public uint BeatmapId { get; set; }
        public uint BeatmapsetId { get; set; }
        public string Artist { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Creator { get; set; } = string.Empty;
        public int Ruleset { get; set; }
        public int Status { get; set; }
        public double StarRating { get; set; }
        public long TotalLength { get; set; }
        public long HitLength { get; set; }
        public double Bpm { get; set; }
        public double CircleSize { get; set; }
        public double ApproachRate { get; set; }
        public double OverallDifficulty { get; set; }
        public double DrainRate { get; set; }
        public long MaxCombo { get; set; }
        public long PlayCount { get; set; }
        public long PassCount { get; set; }

        public long ObjectCount { get; set; }

        public DateTime? RankedAt { get; set; }

        public DateTime? UpdatedAt { get; set; }
    }

    private sealed class MedalProgressRow
    {
        public uint MedalId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Grouping { get; set; } = string.Empty;
        public int Ordering { get; set; }
        public DateTime? UnlockedAt { get; set; }
    }

    private sealed class HistoryRow
    {
        public long Status { get; set; }
        public string Reason { get; set; } = string.Empty;
        public DateTime? At { get; set; }
        public long Period { get; set; }
        public string? BannerName { get; set; }
    }

    private sealed class ReportRow
    {
        public uint ReportId { get; set; }
        public uint ReporterId { get; set; }
        public string ReporterName { get; set; } = string.Empty;
        public uint UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string? Type { get; set; }
        public ulong? ReportableId { get; set; }
        public string? Reason { get; set; }
        public string Comments { get; set; } = string.Empty;
        public DateTime At { get; set; }
    }
}
