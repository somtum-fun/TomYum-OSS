using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>A lazer play with Relax, as the website stored it (ppy's processor gives it no pp).</summary>
public sealed class LazerRelaxPlay
{
    public ulong ScoreId { get; set; }

    public uint UserId { get; set; }

    public uint BeatmapId { get; set; }

    public int Ruleset { get; set; }

    public string Data { get; set; } = string.Empty;

    public long MaxCombo { get; set; }

    public string Grade { get; set; } = string.Empty;

    public bool Passed { get; set; }

    public long TotalScore { get; set; }

    public long LegacyTotalScore { get; set; }

    public DateTime EndedAt { get; set; }
}

/// <summary>Lazer plays with Relax, for TomYum's relax leaderboards (the website doesn't rank them).</summary>
public sealed class OsuWebLazerRelax
{
    /// <summary>The <c>osu_counts</c> row holding the last website score looked at.</summary>
    public const string MarkName = "tomyum_lazer_relax_mark";

    private readonly OsuWebDbContext _db;

    public OsuWebLazerRelax(OsuWebDbContext db)
    {
        _db = db;
    }

    /// <summary>Lazer scores with RX after <paramref name="afterId"/> in osu!, taiko or catch (relax has no mania), oldest first.</summary>
    public async Task<IReadOnlyList<LazerRelaxPlay>> AfterAsync(ulong afterId, int limit, CancellationToken cancellationToken = default) =>
        await _db.Database.SqlQueryRaw<LazerRelaxPlay>(
            """
            SELECT s.id AS ScoreId, s.user_id AS UserId, s.beatmap_id AS BeatmapId, CAST(s.ruleset_id AS SIGNED) AS Ruleset,
              CAST(s.data AS CHAR) AS Data, CAST(s.max_combo AS SIGNED) AS MaxCombo, s.`rank` AS Grade, s.passed AS Passed,
              CAST(s.total_score AS SIGNED) AS TotalScore, CAST(COALESCE(s.legacy_total_score, 0) AS SIGNED) AS LegacyTotalScore, s.ended_at AS EndedAt
            FROM scores s
            WHERE s.id > {0} AND s.legacy_score_id IS NULL AND s.ruleset_id < 3
              AND JSON_CONTAINS(JSON_EXTRACT(s.data, '$.mods[*].acronym'), '"RX"')
            ORDER BY s.id LIMIT {1}
            """, afterId, limit).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The newest website score (where following starts the first time).</summary>
    public async Task<ulong> NewestScoreIdAsync(CancellationToken cancellationToken = default) =>
        await _db.Scores.AsNoTracking().MaxAsync(s => (ulong?)s.Id, cancellationToken).ConfigureAwait(false) ?? 0;

    public async Task<ulong?> MarkAsync(CancellationToken cancellationToken = default) =>
        (await _db.Database.SqlQuery<ulong>($"SELECT count AS Value FROM osu_counts WHERE name = {MarkName}")
            .ToListAsync(cancellationToken).ConfigureAwait(false)) is [var mark] ? mark : null;

    public Task SetMarkAsync(ulong scoreId, CancellationToken cancellationToken = default) =>
        _db.Database.ExecuteSqlAsync(
            $"INSERT INTO osu_counts (name, count) VALUES ({MarkName}, {scoreId}) ON DUPLICATE KEY UPDATE count = {scoreId}", cancellationToken);
}
