using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// The daily rank history behind the website's profile rank graph (ppy's own servers fill it from
/// a daily job TomYum doesn't have). <c>osu_user_performance_rank</c> holds 90 days per player and
/// ruleset in a ring of columns <c>r0</c>–<c>r89</c>; <c>osu_counts</c>'s
/// <c>pp_rank_column_&lt;ruleset&gt;</c> is the column written last (osu-web's <c>RankHistory</c>).
/// Also keeps each player's best rank (<c>osu_user_performance_rank_highest</c>).
/// </summary>
public sealed class OsuWebRankHistory
{
    /// <summary>Names the website uses for the rulesets (rank history, <c>osu_counts</c>).</summary>
    public static readonly IReadOnlyList<string> Rulesets = ["osu", "taiko", "fruits", "mania"];

    private const string DoneName = "tomyum_rank_history_day";

    private const string Visible = "u.user_type <> 1 AND u.user_warnings = 0";

    private readonly OsuWebDbContext _db;

    public OsuWebRankHistory(OsuWebDbContext db) => _db = db;

    /// <summary>
    /// Records today's ranks unless that's done already (UTC days). Returns whether it recorded them.
    /// Run it any number of times a day.
    /// </summary>
    public async Task<bool> RecordIfDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (!await IsDueAsync(now, cancellationToken).ConfigureAwait(false))
            return false;

        var today = long.Parse(now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        for (var ruleset = 0; ruleset < Rulesets.Count; ruleset++)
            await RecordAsync(ruleset, cancellationToken).ConfigureAwait(false);
        await SetCountAsync(DoneName, today, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Whether today's ranks (UTC) haven't been recorded yet.</summary>
    public async Task<bool> IsDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var today = long.Parse(now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        // Never recorded (a new database) counts as due.
        return (await CountAsync(DoneName, cancellationToken).ConfigureAwait(false) ?? 0) < today;
    }

    /// <summary>Writes every player's current global rank into the next day's column for <paramref name="ruleset"/>.</summary>
    public async Task RecordAsync(int ruleset, CancellationToken cancellationToken = default)
    {
        var columnName = $"pp_rank_column_{Rulesets[ruleset]}";
        var previous = await CountAsync(columnName, cancellationToken).ConfigureAwait(false);
        var column = previous is { } last ? (int)((last + 1) % 90) : 0;
        var r = "r" + column.ToString(CultureInfo.InvariantCulture);
        var stats = OsuWebScores.StatsTable(ruleset);
        var mode = ruleset.ToString(CultureInfo.InvariantCulture);

        // Only fixed names (the column, the stats table, the ruleset number) are spliced in.
        var active = RankingInactivity.SqlCondition("s.last_played", DateTimeOffset.UtcNow);
        var ranked = $"""
            SELECT s.user_id AS user_id, ROW_NUMBER() OVER (ORDER BY s.rank_score DESC, s.user_id) AS position
            FROM `{stats}` s JOIN phpbb_users u ON u.user_id = s.user_id
            WHERE {Visible} AND s.rank_score > 0{active}
            """;
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable EF1002 // see above
        // The column is a day 90 days ago: clear it, then fill in today's ranks.
        await _db.Database.ExecuteSqlRawAsync($"UPDATE osu_user_performance_rank SET `{r}` = 0 WHERE mode = {mode}", cancellationToken).ConfigureAwait(false);
        await _db.Database.ExecuteSqlRawAsync(
            $"""
            INSERT INTO osu_user_performance_rank (user_id, mode, `{r}`)
            SELECT ranked.user_id, {mode}, ranked.position FROM ({ranked}) ranked
            ON DUPLICATE KEY UPDATE `{r}` = VALUES(`{r}`)
            """, cancellationToken).ConfigureAwait(false);
        await _db.Database.ExecuteSqlRawAsync(
            $"""
            INSERT INTO osu_user_performance_rank_highest (user_id, mode, `rank`)
            SELECT ranked.user_id, {mode}, ranked.position FROM ({ranked}) ranked
            ON DUPLICATE KEY UPDATE updated_at = IF(VALUES(`rank`) < `rank`, CURRENT_TIMESTAMP, updated_at), `rank` = LEAST(`rank`, VALUES(`rank`))
            """, cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
        await SetCountAsync(columnName, column, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<long?> CountAsync(string name, CancellationToken cancellationToken)
    {
        var rows = await _db.Database.SqlQuery<long>($"SELECT CAST(count AS SIGNED) AS Value FROM osu_counts WHERE name = {name}")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    private Task<int> SetCountAsync(string name, long count, CancellationToken cancellationToken) =>
        _db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO osu_counts (name, count) VALUES ({name}, {count}) ON DUPLICATE KEY UPDATE count = VALUES(count)",
            cancellationToken);
}
