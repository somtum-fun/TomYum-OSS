using Microsoft.EntityFrameworkCore;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Performance;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Achievements;

/// <summary>What the medal rebuild changed.</summary>
public sealed record MedalRebuildResult(int Scores, int Added, int Removed, int LegacyCopiesRemoved);

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

    private readonly TomYumDbContext _tomyum;
    private readonly OsuWebDbContext _osuWeb;
    private readonly OsuWebMedals _medals;
    private readonly IBeatmapRepository _beatmaps;
    private readonly IUserRepository _users;
    private readonly IPerformanceCalculator? _performance;

    public MedalRebuild(
        TomYumDbContext tomyum,
        OsuWebDbContext osuWeb,
        OsuWebMedals medals,
        IBeatmapRepository beatmaps,
        IUserRepository users,
        IPerformanceCalculator? performance = null)
    {
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
