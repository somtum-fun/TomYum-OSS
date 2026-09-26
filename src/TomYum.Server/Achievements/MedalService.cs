using TomYum.Domain.Repositories;

namespace TomYum.Server.Achievements;

/// <summary>
/// Evaluates the <see cref="MedalCatalog"/> against a passed score and unlocks any newly-earned medals,
/// then formats them for the score-submission response. Clean-room port of bancho.py's
/// <c>unlock_new_achievements</c> + <c>format_achievements</c>.
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
    /// Serializes medals for the <c>achievements-new:</c> field: each medal as <c>file+name+desc</c>,
    /// joined with <c>/</c>. Returns an empty string when there are none.
    /// </summary>
    public static string FormatAchievementsNew(IReadOnlyList<Medal> medals)
    {
        ArgumentNullException.ThrowIfNull(medals);
        return string.Join("/", medals.Select(m => $"{m.File}+{m.Name}+{m.Description}"));
    }
}
