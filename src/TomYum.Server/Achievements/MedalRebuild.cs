using Microsoft.EntityFrameworkCore;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Performance;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Achievements;

/// <summary>What the medal rebuild changed.</summary>
public sealed record MedalRebuildResult(int Scores, int Added, int Removed, int LegacyCopiesRemoved);

/// <summary>What the second rebuild (lazer medals, pack medals, old notifications) changed.</summary>
public sealed record LazerMedalRebuildResult(int LazerScores, int Added, int Removed, int PackMedals, int Notifications);

/// <summary>
/// Recalculates the medals TomYum gave for stable scores, once, after they moved to osu!'s ids and
/// rules: earlier ones were judged on the map's no-mod standard star rating (wrong for converts and
/// mods) and copied to the website under bancho.py's numbers, which the website shows as other
/// medals. The website copies under the old numbers go, every stored stable score is judged again,
/// and TomYum's and the website's unlocks are set to the result. Medals ppy's score processor gave
/// for lazer scores are left alone. Runs on the first start with osu!'s ids (marked in
/// <c>osu_counts</c>).
/// </summary>
public sealed class MedalRebuild
{
    public const string DoneName = "tomyum_medals_official";

    /// <summary>The second pass: lazer medals and pack medals judged again, old notifications cleared.</summary>
    public const string LazerDoneName = "tomyum_medals_official_lazer";

    private readonly TomYumDbContext _tomyum;
    private readonly OsuWebDbContext _osuWeb;
    private readonly OsuWebMedals _medals;
    private readonly IBeatmapRepository _beatmaps;
    private readonly IUserRepository _users;
    private readonly IPerformanceCalculator? _performance;
    private readonly PackMedalAwarder? _packs;

    public MedalRebuild(
        TomYumDbContext tomyum,
        OsuWebDbContext osuWeb,
        OsuWebMedals medals,
        IBeatmapRepository beatmaps,
        IUserRepository users,
        IPerformanceCalculator? performance = null,
        PackMedalAwarder? packs = null)
    {
        _packs = packs;
        _tomyum = tomyum;
        _osuWeb = osuWeb;
        _medals = medals;
        _beatmaps = beatmaps;
        _users = users;
        _performance = performance;
    }

    /// <summary>Runs the rebuild unless it's been done; null when it had been.</summary>
    public async Task<MedalRebuildResult?> RunIfPendingAsync(CancellationToken cancellationToken = default)
    {
        var done = await _osuWeb.Database.SqlQuery<long>($"SELECT COUNT(*) AS Value FROM osu_counts WHERE name = {DoneName}")
            .SingleAsync(cancellationToken).ConfigureAwait(false) > 0;
        if (done)
            return null;

        var result = await RunAsync(cancellationToken).ConfigureAwait(false);
        await _osuWeb.Database.ExecuteSqlAsync(
            $"INSERT INTO osu_counts (name, count) VALUES ({DoneName}, 1) ON DUPLICATE KEY UPDATE count = 1", cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// The second pass, once: while the catalog had bancho.py's numbers (and modes), ppy's processor
    /// gave lazer plays medals of other rulesets and the website pack medals for packs that only
    /// shared a local number. Lazer plays' skill and mod introduction medals are judged again (the
    /// processor gets them right from now on), pack medals are recalculated from complete packs,
    /// and the medal notifications from before (which name the wrong medals) are removed.
    /// </summary>
    public async Task<LazerMedalRebuildResult?> RunLazerIfPendingAsync(CancellationToken cancellationToken = default)
    {
        var done = await _osuWeb.Database.SqlQuery<long>($"SELECT COUNT(*) AS Value FROM osu_counts WHERE name = {LazerDoneName}")
            .SingleAsync(cancellationToken).ConfigureAwait(false) > 0;
        if (done)
            return null;

        var now = DateTime.UtcNow;
        var notifications = await _osuWeb.Database.ExecuteSqlAsync(
            $"DELETE FROM user_notifications WHERE notification_id IN (SELECT id FROM notifications WHERE name = 'user_achievement_unlock' AND created_at < {now})",
            cancellationToken).ConfigureAwait(false);
        notifications = await _osuWeb.Database.ExecuteSqlAsync(
            $"DELETE FROM notifications WHERE name = 'user_achievement_unlock' AND created_at < {now}", cancellationToken).ConfigureAwait(false);

        // Lazer plays, judged like stable ones.
        var plays = await _osuWeb.Database.SqlQuery<LazerPlay>(
            $"""
            SELECT CAST(s.user_id AS SIGNED) AS UserId, CAST(s.ruleset_id AS SIGNED) AS Ruleset, CAST(s.beatmap_id AS SIGNED) AS BeatmapId,
              CAST(s.max_combo AS SIGNED) AS MaxCombo, CAST(s.data AS CHAR) AS Data, s.ended_at AS EndedAt, CAST(b.approved AS SIGNED) AS Approved
            FROM scores s
            JOIN osu_beatmaps b ON b.beatmap_id = s.beatmap_id
            JOIN phpbb_users u ON u.user_id = s.user_id
            WHERE s.legacy_score_id IS NULL AND s.passed = 1 AND u.user_type <> 1 AND u.user_warnings = 0
            ORDER BY s.ended_at, s.id
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        var lazerEarned = new Dictionary<(int UserId, int MedalId), (DateTime At, int BeatmapId)>();
        foreach (var play in plays)
        {
            var status = LazerMedalJudge.Status((int)play.Approved);
            if (status is not (RankedStatus.Ranked or RankedStatus.Approved or RankedStatus.Qualified or RankedStatus.Loved))
                continue;
            var facts = LazerMedalJudge.Read(play.Data, (int)play.MaxCombo);
            double? stars = null;
            if (_performance is IAccuracyPerformance withStars && !facts.UnrankedMods)
            {
                try
                {
                    stars = await withStars.StarRatingAsync((int)play.BeatmapId, (int)play.Ruleset, (int)facts.Mods, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
                {
                    // Unknown star rating: no skill medal from this play.
                }
            }
            foreach (var medal in LazerMedalJudge.Earned(facts, (int)play.Ruleset, stars, status, (int)play.BeatmapId))
                lazerEarned.TryAdd(((int)play.UserId, medal.Id), (DateTime.SpecifyKind(play.EndedAt, DateTimeKind.Utc), (int)play.BeatmapId));
        }

        // Medals of these kinds on the website stay only if a stable or lazer play earns them.
        var stable = (await _tomyum.UserAchievements.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(u => (u.UserId, u.AchievementId)).ToHashSet();
        var judged = MedalCatalog.All.Where(m => !m.File.StartsWith("osu-combo-", StringComparison.Ordinal)).Select(m => m.Id).ToHashSet();
        var onWebsite = await _osuWeb.Database.SqlQuery<UnlockRow>(
            $"SELECT CAST(user_id AS SIGNED) AS UserId, CAST(achievement_id AS SIGNED) AS MedalId FROM osu_user_achievements")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var removed = 0;
        foreach (var row in onWebsite.Where(r => judged.Contains((int)r.MedalId)))
        {
            var key = ((int)row.UserId, (int)row.MedalId);
            if (!stable.Contains(key) && !lazerEarned.ContainsKey(key))
                removed += await _medals.RemoveUnlockAsync(key.Item1, key.Item2, cancellationToken).ConfigureAwait(false);
        }
        var present = onWebsite.Select(r => ((int)r.UserId, (int)r.MedalId)).ToHashSet();
        var added = 0;
        foreach (var ((userId, medalId), (at, beatmapId)) in lazerEarned.Where(e => !present.Contains(e.Key)))
        {
            await _medals.InsertUnlockAsync(userId, medalId, beatmapId, at, cancellationToken).ConfigureAwait(false);
            added++;
        }

        // Pack medals: all recalculated from complete packs.
        var packMedals = 0;
        if (_packs is not null)
        {
            var packIds = string.Join(',', PackMedalCatalog.All.Select(m => m.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
#pragma warning disable EF1002 // numbers only
            await _osuWeb.Database.ExecuteSqlRawAsync($"DELETE FROM osu_user_achievements WHERE achievement_id IN ({packIds})", cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
            foreach (var (userId, medalId, at) in await _packs.EarnedAsync(cancellationToken).ConfigureAwait(false))
            {
                await _medals.InsertUnlockAsync(userId, medalId, null, at, cancellationToken).ConfigureAwait(false);
                packMedals++;
            }
        }

        await _osuWeb.Database.ExecuteSqlAsync(
            $"INSERT INTO osu_counts (name, count) VALUES ({LazerDoneName}, 1) ON DUPLICATE KEY UPDATE count = 1", cancellationToken).ConfigureAwait(false);
        return new LazerMedalRebuildResult(plays.Count, added, removed, packMedals, notifications);
    }

    private sealed class LazerPlay
    {
        public long UserId { get; set; }
        public long Ruleset { get; set; }
        public long BeatmapId { get; set; }
        public long MaxCombo { get; set; }
        public string? Data { get; set; }
        public DateTime EndedAt { get; set; }
        public long Approved { get; set; }
    }

    private sealed class UnlockRow
    {
        public long UserId { get; set; }
        public long MedalId { get; set; }
    }

    /// <summary>The rebuild itself (safe to repeat).</summary>
    public async Task<MedalRebuildResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var old = await _tomyum.UserAchievements.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

        // 1. Website copies made under bancho.py's numbers.
        var legacyRemoved = 0;
        foreach (var unlock in old)
        {
            if (MedalCatalog.LegacyIds.TryGetValue(unlock.AchievementId, out var legacyId) && legacyId != unlock.AchievementId)
                legacyRemoved += await _medals.RemoveLegacyCopyAsync(unlock.UserId, legacyId, unlock.UnlockedAt, cancellationToken).ConfigureAwait(false);
        }

        // 2. Every stored stable play judged again, oldest first (the first qualifying play's time is the unlock time).
        var scores = await _tomyum.Scores.AsNoTracking()
            .Where(s => s.Passed && s.UserId > 0 && s.Mode >= 0 && s.Mode < 4)
            .OrderBy(s => s.SubmittedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var earned = new Dictionary<(int UserId, int MedalId), (DateTime At, int BeatmapId)>();
        var maps = new Dictionary<int, Beatmap?>();
        var restricted = new Dictionary<int, bool>();
        foreach (var score in scores)
        {
            if (!restricted.TryGetValue(score.UserId, out var isRestricted))
                restricted[score.UserId] = isRestricted = (await _users.GetByIdAsync(score.UserId, cancellationToken).ConfigureAwait(false))?.IsRestricted ?? true;
            if (!maps.TryGetValue(score.BeatmapId, out var beatmap))
                maps[score.BeatmapId] = beatmap = await _beatmaps.GetByIdAsync(score.BeatmapId, cancellationToken).ConfigureAwait(false);
            if (beatmap is null || !MedalService.Eligible(true, beatmap.Status, (Mods)score.Mods, isRestricted))
                continue;

            var stars = await MedalService.StarRatingAsync(_performance, beatmap, score.Mode, score.Mods, cancellationToken).ConfigureAwait(false);
            var check = new MedalCheck(score.Mode, (Mods)score.Mods, score.MaxCombo, stars, score.Perfect, beatmap.Status, beatmap.Id);
            foreach (var medal in MedalCatalog.All)
            {
                if (medal.Condition(check))
                    earned.TryAdd((score.UserId, medal.Id), (score.SubmittedAt.UtcDateTime, beatmap.Id));
            }
        }

        // 3. TomYum's unlocks and their website copies set to the result.
        var before = old.ToDictionary(u => (u.UserId, u.AchievementId), u => u.UnlockedAt);
        var removed = 0;
        foreach (var ((userId, medalId), unlockedAt) in before.Where(b => !earned.ContainsKey(b.Key)))
        {
            await _tomyum.UserAchievements.Where(u => u.UserId == userId && u.AchievementId == medalId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            // Only the copy TomYum made (same time); a lazer medal with this id stays.
            await _medals.RemoveLegacyCopyAsync(userId, medalId, unlockedAt, cancellationToken).ConfigureAwait(false);
            removed++;
        }

        var added = 0;
        foreach (var ((userId, medalId), (at, beatmapId)) in earned.Where(e => !before.ContainsKey(e.Key)))
        {
            _tomyum.UserAchievements.Add(new UserAchievement { UserId = userId, AchievementId = medalId, UnlockedAt = at });
            await _tomyum.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _tomyum.ChangeTracker.Clear();
            await _medals.InsertUnlockAsync(userId, medalId, beatmapId, at, cancellationToken).ConfigureAwait(false);
            added++;
        }

        return new MedalRebuildResult(scores.Count, added, removed, legacyRemoved);
    }
}
