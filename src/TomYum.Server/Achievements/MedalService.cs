using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Performance;

namespace TomYum.Server.Achievements;

/// <summary>
/// Evaluates the <see cref="MedalCatalog"/> against a passed score and unlocks any newly-earned medals,
/// then formats them for the score-submission response (the client's unlock popups).
/// </summary>
public static class MedalService
{
    /// <summary>
    /// Unlocks every catalog medal whose condition the score satisfies and that the user hasn't already
    /// earned, persisting each new unlock. Returns the newly-unlocked medals (empty if none). The caller
    /// is responsible for the gate (score passed, map awards ranked pp, player not restricted).
    /// </summary>
    public static async Task<IReadOnlyList<Medal>> AwardAsync(
        IUserAchievementRepository repository,
        int userId,
        MedalCheck check,
        DateTime unlockedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var unlocked = await repository.GetUnlockedIdsAsync(userId, cancellationToken).ConfigureAwait(false);

        List<Medal>? newlyUnlocked = null;
        foreach (var medal in MedalCatalog.All)
        {
            if (unlocked.Contains(medal.Id) || !medal.Condition(check))
            {
                continue;
            }

            // AddAsync is idempotent; only count it as newly-unlocked when the row was actually inserted.
            if (await repository.AddAsync(userId, medal.Id, unlockedAt, cancellationToken).ConfigureAwait(false))
            {
                (newlyUnlocked ??= []).Add(medal);
            }
        }

        return (IReadOnlyList<Medal>?)newlyUnlocked ?? [];
    }

    /// <summary>
    /// Whether a play can earn medals at all (osu!'s rules): passed, on a map with a leaderboard
    /// (ranked, approved, qualified, loved), not relax/autopilot/autoplay (which osu! never ranks),
    /// and the player isn't restricted.
    /// </summary>
    public static bool Eligible(bool passed, RankedStatus status, Mods mods, bool restricted) =>
        passed && !restricted
        && status is RankedStatus.Ranked or RankedStatus.Approved or RankedStatus.Qualified or RankedStatus.Loved
        && (mods & (Mods.Relax | Mods.Autopilot | Mods.Autoplay)) == 0;

    /// <summary>
    /// The star rating medals judge a play on: the map in the ruleset played, with the play's mods.
    /// Without a calculator that can tell, the stored rating when it's the same thing (the map's own
    /// ruleset, no mod that changes difficulty); otherwise null (no skill medal).
    /// </summary>
    public static async Task<double?> StarRatingAsync(IPerformanceCalculator? calculator, Beatmap beatmap, int ruleset, int mods, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        if (calculator is IAccuracyPerformance withStars)
        {
            try
            {
                if (await withStars.StarRatingAsync(beatmap.Id, ruleset, mods, cancellationToken).ConfigureAwait(false) is { } stars)
                    return stars;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
            {
                // The .osu couldn't be fetched: fall back below.
            }
        }

        const Mods changesDifficulty = Mods.Easy | Mods.HardRock | Mods.DoubleTime | Mods.Nightcore | Mods.HalfTime | Mods.Flashlight;
        return ruleset == beatmap.Mode && ((Mods)mods & changesDifficulty) == 0 && beatmap.StarRating > 0 ? beatmap.StarRating : null;
    }

    /// <summary>
    /// Serializes medals for the <c>achievements-new:</c> field: each medal as <c>file+name+desc</c>,
    /// joined with <c>/</c>. Returns an empty string when there are none.
    /// </summary>
    public static string FormatAchievementsNew(IReadOnlyList<Medal> medals)
    {
        ArgumentNullException.ThrowIfNull(medals);
        return string.Join("/", medals.Select(m => $"{m.File}+{m.Name}+{m.Description}"));
    }
}
