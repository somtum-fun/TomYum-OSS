using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>A player's stats in one ruleset, as their website profile shows them.</summary>
public sealed record PlayerCard(
    int UserId,
    string Username,
    string Country,
    int Ruleset,
    double Pp,
    int? GlobalRank,
    int? CountryRank,
    double Accuracy,
    long PlayCount,
    long PlaySeconds,
    long RankedScore,
    long TotalScore,
    double Level,
    int MaxCombo,
    int CountSsh,
    int CountSs,
    int CountSh,
    int CountS,
    int CountA,
    bool IsRestricted,
    DateTime JoinedAt);

/// <summary>Where a new best landed on its map: its position, and who was #1 before (null: nobody).</summary>
public sealed record ScorePlacement(int Position, int? PreviousLeaderId);

/// <summary>A website score (stable or lazer) with its map, for listings and announcements.</summary>
public sealed record ScoreCard(
    ulong ScoreId,
    int UserId,
    string Username,
    int Ruleset,
    int BeatmapId,
    int BeatmapsetId,
    string Artist,
    string Title,
    string Version,
    string Creator,
    double StarRating,
    int BeatmapStatus,
    IReadOnlyList<string> Mods,
    double Accuracy,
    int MaxCombo,
    int BeatmapMaxCombo,
    string Grade,
    bool Passed,
    double? Pp,
    long TotalScore,
    long LegacyTotalScore,
    bool IsStable,
    int Great,
    int Ok,
    int Meh,
    int Miss,
    DateTime EndedAt,
    // How fast the map played (lazer's DT/HT keep the speed picked: OsuWebCommunity.ReadRate); null: unknown, or it changed during the play.
    double? Rate = null)
{
    /// <summary>"Artist - Title (Mapper) [Version]", as lazer shows a map.</summary>
    public string MapName => string.IsNullOrEmpty(Creator) ? $"{Artist} - {Title} [{Version}]" : $"{Artist} - {Title} ({Creator}) [{Version}]";
}

/// <summary>A player on the ranking.</summary>
public sealed record RankingEntry(int Position, int UserId, string Username, string Country, double Pp, double Accuracy, long PlayCount);

/// <summary>A medal a player unlocked.</summary>
public sealed record MedalUnlock(int UserId, string Username, int MedalId, string Name, string Description, DateTime UnlockedAt);

/// <summary>A new account.</summary>
public sealed record NewPlayer(int UserId, string Username, string Country, DateTime JoinedAt);

/// <summary>A beatmap set's headline details.</summary>
public sealed record BeatmapsetCard(int BeatmapsetId, string Artist, string Title, string Creator, int Status, IReadOnlyList<string> Versions);

/// <summary>
/// Read-only queries over osu-web's database for the Discord bot: profiles, scores, rankings and
/// map leaderboards the way the website shows them (stable and lazer together), and the feeds of
/// new scores, medals and players it announces. Restricted players are left out everywhere except
/// their own profile lookups, like on the website.
/// </summary>
public sealed partial class OsuWebCommunity
{
    /// <summary>Statuses with a leaderboard: ranked, approved, qualified, loved.</summary>
    public static readonly IReadOnlySet<int> LeaderboardStatuses = new HashSet<int> { 1, 2, 3, 4 };

    private const string Visible = "u.user_type <> 1 AND u.user_warnings = 0";

    private readonly OsuWebDbContext _db;

    private readonly IStarRatings? _stars;

    /// <param name="db">The website's database.</param>
    /// <param name="stars">Star ratings with mods (else cards show the map's own rating).</param>
    public OsuWebCommunity(OsuWebDbContext db, IStarRatings? stars = null)
    {
        _db = db;
        _stars = stars;
    }

    /// <summary>Finds a player by name (case-insensitive; <c>_</c> may stand for a space).</summary>
    public async Task<(int UserId, string Username)?> FindUserAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var clean = name.Trim().ToLowerInvariant();
        if (clean.Length == 0)
            return null;

        var spaced = clean.Replace('_', ' ');
        var match = await _db.Users.AsNoTracking()
            .Where(u => u.UsernameClean == clean || u.UsernameClean == spaced)
            .OrderBy(u => u.UsernameClean == clean ? 0 : 1)
            .Select(u => new { u.UserId, u.Username })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (match is null && int.TryParse(clean, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            match = await _db.Users.AsNoTracking().Where(u => u.UserId == (uint)id)
                .Select(u => new { u.UserId, u.Username })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
        return match is null ? null : ((int)match.UserId, match.Username);
    }

    /// <summary>The ruleset the player has the most plays in (0 when they haven't played).</summary>
    public async Task<int> MainRulesetAsync(int userId, CancellationToken cancellationToken = default)
    {
        var best = (0, -1L);
        for (var ruleset = 0; ruleset < 4; ruleset++)
        {
            var sql = $"SELECT CAST(playcount AS SIGNED) AS Value FROM `{OsuWebScores.StatsTable(ruleset)}` WHERE user_id = {{0}}";
            var plays = (await _db.Database.SqlQueryRaw<long>(sql, userId).ToListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(-1);
            if (plays > best.Item2)
                best = (ruleset, plays);
        }
        return best.Item1;
    }

    /// <summary>The player's profile stats in <paramref name="ruleset"/>, or null when there's no such player.</summary>
    public async Task<PlayerCard?> GetPlayerAsync(int userId, int ruleset, CancellationToken cancellationToken = default)
    {
        var table = OsuWebScores.StatsTable(ruleset);
        var sql = $$"""
            SELECT u.user_id AS UserId, u.username AS Username, u.country_acronym AS Country,
              (u.user_type = 1 OR u.user_warnings <> 0) AS Restricted, u.user_regdate AS RegisteredAt,
              COALESCE(s.rank_score, 0) AS Pp, COALESCE(s.accuracy_new, 0) AS Accuracy,
              CAST(COALESCE(s.playcount, 0) AS SIGNED) AS PlayCount, COALESCE(s.total_seconds_played, 0) AS PlaySeconds,
              COALESCE(s.ranked_score, 0) AS RankedScore, COALESCE(s.total_score, 0) AS TotalScore,
              COALESCE(s.level, 1) AS Level, CAST(COALESCE(s.max_combo, 0) AS SIGNED) AS MaxCombo,
              CAST(COALESCE(s.xh_rank_count, 0) AS SIGNED) AS CountSsh, CAST(COALESCE(s.x_rank_count, 0) AS SIGNED) AS CountSs,
              CAST(COALESCE(s.sh_rank_count, 0) AS SIGNED) AS CountSh, CAST(COALESCE(s.s_rank_count, 0) AS SIGNED) AS CountS,
              CAST(COALESCE(s.a_rank_count, 0) AS SIGNED) AS CountA
            FROM phpbb_users u LEFT JOIN `{{table}}` s ON s.user_id = u.user_id
            WHERE u.user_id = {0}
            """;
        var row = (await _db.Database.SqlQueryRaw<PlayerRow>(sql, userId).ToListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        if (row is null)
            return null;

        int? globalRank = null, countryRank = null;
        if (row.Pp > 0 && !row.Restricted)
        {
            // Compared in SQL against the player's own row: the column is a float, and a rounded copy
            // passed back in could rank the player below themselves.
            var above = $$"""
                SELECT COUNT(*) AS Value FROM `{{table}}` s JOIN phpbb_users u ON u.user_id = s.user_id
                WHERE {{Visible}} AND s.user_id <> {0} AND s.rank_score > (SELECT own.rank_score FROM `{{table}}` own WHERE own.user_id = {0})
                """;
            globalRank = (int)(await _db.Database.SqlQueryRaw<long>(above, userId).SingleAsync(cancellationToken).ConfigureAwait(false)) + 1;
            countryRank = (int)(await _db.Database.SqlQueryRaw<long>(above + " AND u.country_acronym = {1}", userId, row.Country)
                .SingleAsync(cancellationToken).ConfigureAwait(false)) + 1;
        }

        return new PlayerCard(
            (int)row.UserId, row.Username, row.Country, ruleset, row.Pp, globalRank, countryRank, row.Accuracy,
            row.PlayCount, row.PlaySeconds, row.RankedScore, row.TotalScore, row.Level, (int)row.MaxCombo,
            (int)row.CountSsh, (int)row.CountSs, (int)row.CountSh, (int)row.CountS, (int)row.CountA,
            row.Restricted, DateTimeOffset.FromUnixTimeSeconds(row.RegisteredAt).UtcDateTime);
    }

    /// <summary>The player's latest scores in <paramref name="ruleset"/>, newest first (fails included).</summary>
    public Task<IReadOnlyList<ScoreCard>> GetRecentAsync(int userId, int ruleset, int count, CancellationToken cancellationToken = default) =>
        QueryScoresAsync(
            "s.user_id = {0} AND s.ruleset_id = {1}",
            "s.id DESC",
            [userId, ruleset],
            Math.Clamp(count, 1, 50),
            cancellationToken);

    /// <summary>The player's best pp plays in <paramref name="ruleset"/> (one per map), best first.</summary>
    public async Task<IReadOnlyList<ScoreCard>> GetBestAsync(int userId, int ruleset, int count, CancellationToken cancellationToken = default)
    {
        var scores = await QueryScoresAsync(
            "s.user_id = {0} AND s.ruleset_id = {1} AND s.passed = 1 AND s.ranked = 1 AND s.preserve = 1 AND s.pp IS NOT NULL",
            "s.pp DESC",
            [userId, ruleset],
            500,
            cancellationToken).ConfigureAwait(false);
        return scores.GroupBy(s => s.BeatmapId).Select(g => g.First()).Take(Math.Clamp(count, 1, 100)).ToList();
    }

    /// <summary>The top players by pp in <paramref name="ruleset"/>, optionally in one country.</summary>
    public async Task<IReadOnlyList<RankingEntry>> GetRankingAsync(int ruleset, int count, string? country = null, CancellationToken cancellationToken = default)
    {
        var sql = $$"""
            SELECT u.user_id AS UserId, u.username AS Username, u.country_acronym AS Country, s.rank_score AS Pp,
              s.accuracy_new AS Accuracy, CAST(s.playcount AS SIGNED) AS PlayCount
            FROM `{{OsuWebScores.StatsTable(ruleset)}}` s JOIN phpbb_users u ON u.user_id = s.user_id
            WHERE {{Visible}} AND s.rank_score > 0 AND ({0} = '' OR u.country_acronym = {0})
            ORDER BY s.rank_score DESC, u.user_id
            LIMIT {{Math.Clamp(count, 1, 50).ToString(CultureInfo.InvariantCulture)}}
            """;
        var rows = await _db.Database.SqlQueryRaw<RankingRow>(sql, (country ?? string.Empty).ToUpperInvariant())
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select((r, i) => new RankingEntry(i + 1, (int)r.UserId, r.Username, r.Country, r.Pp, r.Accuracy, r.PlayCount)).ToList();
    }

    /// <summary>A map's leaderboard: each player's best score by total score, as on the website.</summary>
    public async Task<IReadOnlyList<ScoreCard>> GetBeatmapLeaderboardAsync(int beatmapId, int ruleset, int count, CancellationToken cancellationToken = default)
    {
        var scores = await QueryScoresAsync(
            $"s.beatmap_id = {{0}} AND s.ruleset_id = {{1}} AND s.passed = 1 AND s.preserve = 1 AND {Visible}",
            "s.total_score DESC, s.id",
            [beatmapId, ruleset],
            500,
            cancellationToken).ConfigureAwait(false);
        return scores.GroupBy(s => s.UserId).Select(g => g.First()).Take(Math.Clamp(count, 1, 50)).ToList();
    }

    /// <summary>A map's ruleset (its own mode) and whether it's on the website.</summary>
    public async Task<int?> GetBeatmapRulesetAsync(int beatmapId, CancellationToken cancellationToken = default)
    {
        var mode = await _db.Beatmaps.AsNoTracking().Where(b => b.BeatmapId == (uint)beatmapId)
            .Select(b => (int?)b.Playmode).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return mode;
    }

    public async Task<BeatmapsetCard?> GetBeatmapsetAsync(int beatmapsetId, CancellationToken cancellationToken = default)
    {
        var set = await _db.Beatmapsets.AsNoTracking().FirstOrDefaultAsync(s => s.BeatmapsetId == (uint)beatmapsetId, cancellationToken).ConfigureAwait(false);
        if (set is null)
            return null;
        var versions = await _db.Beatmaps.AsNoTracking().Where(b => b.BeatmapsetId == (uint)beatmapsetId)
            .OrderBy(b => b.Difficultyrating).Select(b => b.Version).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new BeatmapsetCard(beatmapsetId, set.Artist, set.Title, set.Creator, set.Approved, versions);
    }

    /// <summary>The newest website score id (where the announcement feed starts).</summary>
    public async Task<ulong> GetLatestScoreIdAsync(CancellationToken cancellationToken = default) =>
        await _db.Scores.AsNoTracking().MaxAsync(s => (ulong?)s.Id, cancellationToken).ConfigureAwait(false) ?? 0;

    /// <summary>One website score, whoever set it (restricted players' too: for staff tools), or null.</summary>
    public async Task<ScoreCard?> GetScoreCardAsync(ulong scoreId, CancellationToken cancellationToken = default) =>
        await QueryScoresAsync("s.id = {0}", "s.id", [scoreId], 1, cancellationToken).ConfigureAwait(false) is [var card, ..] ? card : null;

    /// <summary>Scores after <paramref name="afterId"/> by visible players, oldest first.</summary>
    public Task<IReadOnlyList<ScoreCard>> GetScoresAfterAsync(ulong afterId, int limit, CancellationToken cancellationToken = default) =>
        QueryScoresAsync($"s.id > {{0}} AND {Visible}", "s.id", [afterId], Math.Clamp(limit, 1, 500), cancellationToken);

    /// <summary>
    /// Whether <paramref name="score"/> took #1 on its map when it was set: it beats every earlier
    /// score by other visible players, and the #1 before it (if any) was someone else's.
    /// </summary>
    public async Task<bool> TookFirstPlaceAsync(ScoreCard score, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (!score.Passed || !LeaderboardStatuses.Contains(score.BeatmapStatus))
            return false;

        var sql = $$"""
            SELECT s.user_id AS UserId, CAST(s.total_score AS SIGNED) AS TotalScore, s.id AS Id
            FROM scores s JOIN phpbb_users u ON u.user_id = s.user_id
            WHERE s.beatmap_id = {0} AND s.ruleset_id = {1} AND s.passed = 1 AND s.preserve = 1 AND {{Visible}} AND s.id < {2}
            ORDER BY s.total_score DESC, s.id
            """;
        var others = await _db.Database.SqlQueryRaw<LeaderRow>(sql, score.BeatmapId, score.Ruleset, score.ScoreId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (others.Any(o => o.UserId != (uint)score.UserId && o.TotalScore >= score.TotalScore))
            return false;
        // Sorted by score, then age: the first row is the #1 the new score replaces.
        return others.Count == 0 || others[0].UserId != (uint)score.UserId;
    }

    /// <summary>
    /// Where <paramref name="score"/> placed on its map's leaderboard when it was set: null when it
    /// isn't a leaderboard play or not the player's new best there; else its position among the
    /// players' bests (earlier equal scores go first) and who held #1 before it.
    /// </summary>
    public async Task<ScorePlacement?> GetPlacementAsync(ScoreCard score, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (!score.Passed || !LeaderboardStatuses.Contains(score.BeatmapStatus))
            return null;

        var sql = $$"""
            SELECT s.user_id AS UserId, CAST(MAX(s.total_score) AS SIGNED) AS TotalScore, MIN(s.id) AS Id
            FROM scores s JOIN phpbb_users u ON u.user_id = s.user_id
            WHERE s.beatmap_id = {0} AND s.ruleset_id = {1} AND s.passed = 1 AND s.preserve = 1 AND {{Visible}} AND s.id < {2}
            GROUP BY s.user_id
            """;
        var bests = await _db.Database.SqlQueryRaw<LeaderRow>(sql, score.BeatmapId, score.Ruleset, score.ScoreId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (bests.FirstOrDefault(b => b.UserId == (uint)score.UserId) is { } own && own.TotalScore >= score.TotalScore)
            return null;

        var others = bests.Where(b => b.UserId != (uint)score.UserId).ToList();
        var position = 1 + others.Count(b => b.TotalScore >= score.TotalScore);
        var leader = bests.OrderByDescending(b => b.TotalScore).ThenBy(b => b.Id).FirstOrDefault();
        return new ScorePlacement(position, leader is null ? null : (int)leader.UserId);
    }

    /// <summary>When the oldest "achieved rank" activity entry was written (null: there's none).</summary>
    public async Task<DateTime?> FirstRankEventAtAsync(CancellationToken cancellationToken = default) =>
        (await _db.Database.SqlQueryRaw<DateTime?>("SELECT MIN(`date`) AS Value FROM osu_events WHERE text LIKE '%achieved%rank #%'")
            .ToListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    /// <summary>Whether a one-off job named <paramref name="name"/> ran (marked in <c>osu_counts</c>).</summary>
    public async Task<bool> WasDoneAsync(string name, CancellationToken cancellationToken = default) =>
        await _db.Database.SqlQuery<long>($"SELECT COUNT(*) AS Value FROM osu_counts WHERE name = {name}").SingleAsync(cancellationToken).ConfigureAwait(false) > 0;

    public Task MarkDoneAsync(string name, CancellationToken cancellationToken = default) =>
        _db.Database.ExecuteSqlAsync($"INSERT INTO osu_counts (name, count) VALUES ({name}, 1) ON DUPLICATE KEY UPDATE count = 1", cancellationToken);

    /// <summary>Whether ppy's score processor has handled the score (its pp and stats are final).</summary>
    public async Task<bool> IsProcessedAsync(ulong scoreId, CancellationToken cancellationToken = default) =>
        await _db.Database.SqlQuery<long>($"SELECT COUNT(*) AS Value FROM score_process_history WHERE score_id = {scoreId}")
            .SingleAsync(cancellationToken).ConfigureAwait(false) > 0;

    /// <summary>Whether <paramref name="score"/> is the player's best pp on its map so far.</summary>
    public async Task<bool> IsPersonalBestPpAsync(ScoreCard score, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (score.Pp is not { } pp)
            return false;
        var higher = await _db.Scores.AsNoTracking().AnyAsync(s => s.UserId == (uint)score.UserId && s.BeatmapId == (uint)score.BeatmapId
            && s.RulesetId == (ushort)score.Ruleset && s.Id != score.ScoreId && s.Pp != null && s.Pp >= pp, cancellationToken).ConfigureAwait(false);
        return !higher;
    }

    /// <summary>Medals unlocked at or after <paramref name="since"/> by visible players, oldest first.</summary>
    public async Task<IReadOnlyList<MedalUnlock>> GetMedalsSinceAsync(DateTime since, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Database.SqlQueryRaw<MedalRow>(
            $$"""
            SELECT ua.user_id AS UserId, u.username AS Username, ua.achievement_id AS MedalId, a.name AS Name,
              COALESCE(a.description, '') AS Description, ua.`date` AS UnlockedAt
            FROM osu_user_achievements ua
            JOIN osu_achievements a ON a.achievement_id = ua.achievement_id
            JOIN phpbb_users u ON u.user_id = ua.user_id
            WHERE ua.`date` >= {0} AND {{Visible}}
            ORDER BY ua.`date`, ua.user_id, ua.achievement_id
            LIMIT 200
            """, since).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new MedalUnlock(r.UserId, r.Username, r.MedalId, r.Name, r.Description, DateTime.SpecifyKind(r.UnlockedAt, DateTimeKind.Utc))).ToList();
    }

    /// <summary>The newest medal unlock time (where the medal feed starts).</summary>
    public async Task<DateTime> GetLatestMedalTimeAsync(CancellationToken cancellationToken = default)
    {
        var latest = await _db.Database.SqlQueryRaw<DateTime?>("SELECT MAX(`date`) AS Value FROM osu_user_achievements")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        // Before any unlock: a date older than every possible one (and one MySQL accepts).
        return latest is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>The newest account id (where the new-player feed starts).</summary>
    public async Task<int> GetLatestUserIdAsync(CancellationToken cancellationToken = default) =>
        (int)(await _db.Users.AsNoTracking().MaxAsync(u => (uint?)u.UserId, cancellationToken).ConfigureAwait(false) ?? 0);

    /// <summary>Accounts after <paramref name="afterId"/> (bots and banned accounts left out), oldest first.</summary>
    public async Task<IReadOnlyList<NewPlayer>> GetPlayersAfterAsync(int afterId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Users.AsNoTracking()
            .Where(u => u.UserId > (uint)afterId && u.UserType != 1 && !u.UserWarnings)
            .OrderBy(u => u.UserId)
            .Take(200)
            .Select(u => new { u.UserId, u.Username, u.CountryAcronym, u.UserRegdate, u.GroupId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var botGroup = await _db.Groups.AsNoTracking().Where(g => g.Identifier == "bot").Select(g => (uint?)g.GroupId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return rows.Where(r => r.GroupId != botGroup)
            .Select(r => new NewPlayer((int)r.UserId, r.Username, r.CountryAcronym, DateTimeOffset.FromUnixTimeSeconds(r.UserRegdate).UtcDateTime))
            .ToList();
    }

    /// <summary>How recently a player must have been seen to count as online (osu-web's <c>USER_ONLINE_WINDOW</c> default).</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Players the website shows as online (osu-web's <c>User::isOnline</c>: seen within
    /// <see cref="OnlineWindow"/>, presence not hidden). Lazer counts through its website API use.
    /// BanchoBot and restricted players are left out.
    /// </summary>
    public async Task<IReadOnlyList<(int UserId, string Username)>> GetOnlineAsync(CancellationToken cancellationToken = default) =>
        (await GetOnlineSinceAsync(cancellationToken).ConfigureAwait(false)).Select(o => (o.UserId, o.Username)).ToList();

    /// <summary>Names of these players, leaving out unknown, banned and restricted ones.</summary>
    public async Task<IReadOnlyDictionary<int, string>> GetVisibleNamesAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
            return new Dictionary<int, string>();
        var ids = userIds.Select(id => (uint)id).ToList();
        return await _db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.UserId) && u.UserType != 1 && !u.UserWarnings)
            .ToDictionaryAsync(u => (int)u.UserId, u => u.Username, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Like <see cref="GetOnlineAsync"/>, with when each was last seen.</summary>
    public async Task<IReadOnlyList<(int UserId, string Username, DateTime LastSeen)>> GetOnlineSinceAsync(CancellationToken cancellationToken = default)
    {
        var since = DateTimeOffset.UtcNow.Add(-OnlineWindow).ToUnixTimeSeconds();
        var rows = await _db.Database.SqlQueryRaw<OnlineRow>(
            $$"""
            SELECT u.user_id AS UserId, u.username AS Username, CAST(u.user_lastvisit AS SIGNED) AS LastVisit FROM phpbb_users u
            WHERE u.user_lastvisit > {0} AND u.user_allow_viewonline = 1 AND u.user_id <> 1 AND {{Visible}}
            ORDER BY u.username
            LIMIT 500
            """, since).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r => ((int)r.UserId, r.Username, DateTimeOffset.FromUnixTimeSeconds(r.LastVisit).UtcDateTime)).ToList();
    }

    private sealed class OnlineRow
    {
        public uint UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public long LastVisit { get; set; }
    }

    /// <summary>The player's osu-web group identifiers (<c>admin</c>, <c>gmt</c>, <c>nat</c>, <c>bng</c>, …).</summary>
    public async Task<IReadOnlySet<string>> GetGroupsAsync(int userId, CancellationToken cancellationToken = default)
    {
        var groups = await (from membership in _db.UserGroups.AsNoTracking()
                            join grp in _db.Groups.AsNoTracking() on membership.GroupId equals grp.GroupId
                            where membership.UserId == (uint)userId && grp.Identifier != null
                            select grp.Identifier!).ToListAsync(cancellationToken).ConfigureAwait(false);
        return groups.ToHashSet(StringComparer.Ordinal);
    }

    private async Task<IReadOnlyList<ScoreCard>> QueryScoresAsync(string where, string orderBy, object[] parameters, int limit, CancellationToken cancellationToken)
    {
        // Only fixed fragments from this class are spliced in; values go in as parameters.
        var sql = $$"""
            SELECT s.id AS ScoreId, s.user_id AS UserId, u.username AS Username, s.ruleset_id AS Ruleset,
              s.beatmap_id AS BeatmapId, COALESCE(b.beatmapset_id, 0) AS BeatmapsetId,
              COALESCE(bs.artist, '') AS Artist, COALESCE(bs.title, '') AS Title, COALESCE(b.version, '') AS Version,
              COALESCE(bs.creator, '') AS Creator, COALESCE(b.difficultyrating, 0) AS StarRating,
              CAST(COALESCE(b.playmode, 0) AS SIGNED) AS MapRuleset,
              COALESCE(b.approved, 0) AS BeatmapStatus, CAST(COALESCE(b.max_combo, 0) AS SIGNED) AS BeatmapMaxCombo,
              s.accuracy AS Accuracy, CAST(s.max_combo AS SIGNED) AS MaxCombo, s.`rank` AS Grade, s.passed AS Passed,
              s.pp AS Pp, CAST(s.total_score AS SIGNED) AS TotalScore, CAST(s.legacy_total_score AS SIGNED) AS LegacyTotalScore,
              (s.legacy_score_id IS NOT NULL) AS IsStable, CAST(s.data AS CHAR) AS Data, s.ended_at AS EndedAt
            FROM scores s
            JOIN phpbb_users u ON u.user_id = s.user_id
            LEFT JOIN osu_beatmaps b ON b.beatmap_id = s.beatmap_id
            LEFT JOIN osu_beatmapsets bs ON bs.beatmapset_id = b.beatmapset_id
            WHERE {{where}}
            ORDER BY {{orderBy}}
            LIMIT {{limit.ToString(CultureInfo.InvariantCulture)}}
            """;
        var rows = await _db.Database.SqlQueryRaw<ScoreRow>(sql, parameters).ToListAsync(cancellationToken).ConfigureAwait(false);
        var cards = new List<ScoreCard>(rows.Count);
        foreach (var row in rows)
        {
            var card = ToCard(row);
            // The stored rating is the map's own ruleset without mods: others need working out.
            if (_stars is not null && card.BeatmapId > 0 && (card.Ruleset != row.MapRuleset || card.Mods.Any(StarRatingMods.Contains))
                && await _stars.GetAsync(card.BeatmapId, card.Ruleset, card.Mods, cancellationToken).ConfigureAwait(false) is { } stars)
            {
                card = card with { StarRating = stars };
            }
            cards.Add(card);
        }
        return cards;
    }

    /// <summary>Mods that change a map's star rating.</summary>
    public static readonly IReadOnlySet<string> StarRatingMods = new HashSet<string>(StringComparer.Ordinal) { "EZ", "HR", "DT", "NC", "HT", "DC", "FL" };

    private static ScoreCard ToCard(ScoreRow row)
    {
        var (mods, great, ok, meh, miss) = ReadData(row.Data);
        return new ScoreCard(
            row.ScoreId, (int)row.UserId, row.Username, row.Ruleset, (int)row.BeatmapId, (int)row.BeatmapsetId,
            row.Artist, row.Title, row.Version, row.Creator, row.StarRating, row.BeatmapStatus, mods,
            row.Accuracy, (int)row.MaxCombo, (int)row.BeatmapMaxCombo, row.Passed ? row.Grade : "F", row.Passed, row.Pp,
            row.TotalScore, row.LegacyTotalScore, row.IsStable, great, ok, meh, miss,
            DateTime.SpecifyKind(row.EndedAt, DateTimeKind.Utc), ReadRate(row.Data));
    }

    /// <summary>
    /// How fast a score's map played, from its data: lazer's DT/NC and HT/DC keep the speed the
    /// player picked (<c>settings.speed_change</c>, 1.5× and 0.75× unless changed); 1 without them.
    /// Null when the speed changes during the play (Wind Up/Down, Adaptive Speed) or can't be read.
    /// </summary>
    public static double? ReadRate(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return 1.0;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("mods", out var modList) || modList.ValueKind != JsonValueKind.Array)
                return 1.0;

            var rate = 1.0;
            foreach (var mod in modList.EnumerateArray())
            {
                var acronym = mod.ValueKind == JsonValueKind.Object && mod.TryGetProperty("acronym", out var a) ? a.GetString()
                    : mod.ValueKind == JsonValueKind.String ? mod.GetString() : null;
                double? usual = acronym switch
                {
                    "DT" or "NC" => 1.5,
                    "HT" or "DC" => 0.75,
                    "WU" or "WD" or "AS" => double.NaN,
                    _ => null,
                };
                if (usual is not { } value)
                    continue;
                if (double.IsNaN(value))
                    return null;

                rate = mod.ValueKind == JsonValueKind.Object
                    && mod.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object
                    && settings.TryGetProperty("speed_change", out var speed) && speed.TryGetDouble(out var chosen)
                    ? chosen
                    : value;
            }
            return rate;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The mods (without Classic, which marks every stable score) and hit counts from a score's data.</summary>
    public static (IReadOnlyList<string> Mods, int Great, int Ok, int Meh, int Miss) ReadData(string? json)
    {
        var mods = new List<string>();
        int great = 0, ok = 0, meh = 0, miss = 0;
        if (string.IsNullOrEmpty(json))
            return (mods, 0, 0, 0, 0);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("mods", out var modList) && modList.ValueKind == JsonValueKind.Array)
            {
                foreach (var mod in modList.EnumerateArray())
                {
                    var acronym = mod.ValueKind == JsonValueKind.Object && mod.TryGetProperty("acronym", out var a) ? a.GetString()
                        : mod.ValueKind == JsonValueKind.String ? mod.GetString() : null;
                    if (!string.IsNullOrEmpty(acronym) && acronym != "CL")
                        mods.Add(acronym);
                }
            }
            if (root.TryGetProperty("statistics", out var statistics) && statistics.ValueKind == JsonValueKind.Object)
            {
                int Count(string name) => statistics.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) ? n : 0;
                great = Count("great") + Count("perfect");
                ok = Count("ok") + Count("good") + Count("large_tick_hit");
                meh = Count("meh") + Count("small_tick_hit");
                miss = Count("miss") + Count("large_tick_miss");
            }
        }
        catch (JsonException)
        {
            // A score without readable data still shows, just without mods and hit counts.
        }
        return (mods, great, ok, meh, miss);
    }

    private sealed class PlayerRow
    {
        public uint UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public bool Restricted { get; set; }
        public long RegisteredAt { get; set; }
        public double Pp { get; set; }
        public double Accuracy { get; set; }
        public long PlayCount { get; set; }
        public long PlaySeconds { get; set; }
        public long RankedScore { get; set; }
        public long TotalScore { get; set; }
        public double Level { get; set; }
        public long MaxCombo { get; set; }
        public long CountSsh { get; set; }
        public long CountSs { get; set; }
        public long CountSh { get; set; }
        public long CountS { get; set; }
        public long CountA { get; set; }
    }

    private sealed class RankingRow
    {
        public uint UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public double Pp { get; set; }
        public double Accuracy { get; set; }
        public long PlayCount { get; set; }
    }

    private sealed class LeaderRow
    {
        public uint UserId { get; set; }
        public long TotalScore { get; set; }
        public ulong Id { get; set; }
    }

    private sealed class MedalRow
    {
        public int UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public int MedalId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTime UnlockedAt { get; set; }
    }

    private sealed class ScoreRow
    {
        public ulong ScoreId { get; set; }
        public uint UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public int Ruleset { get; set; }
        public uint BeatmapId { get; set; }
        public uint BeatmapsetId { get; set; }
        public string Artist { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Creator { get; set; } = string.Empty;
        public double StarRating { get; set; }

        public long MapRuleset { get; set; }
        public int BeatmapStatus { get; set; }
        public long BeatmapMaxCombo { get; set; }
        public double Accuracy { get; set; }
        public long MaxCombo { get; set; }
        public string Grade { get; set; } = string.Empty;
        public bool Passed { get; set; }
        public double? Pp { get; set; }
        public long TotalScore { get; set; }
        public long LegacyTotalScore { get; set; }
        public bool IsStable { get; set; }
        public string? Data { get; set; }
        public DateTime EndedAt { get; set; }
    }
}
