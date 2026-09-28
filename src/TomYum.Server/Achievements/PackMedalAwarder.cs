using TomYum.Persistence.OsuWeb;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Achievements;

/// <summary>
/// Awards osu!'s pack medals (<see cref="PackMedalCatalog"/>) for stable and lazer plays alike,
/// from the website's scores: after new passes (<see cref="CheckAsync"/>), and all at once when
/// medals are rebuilt (<see cref="EarnedAsync"/>). A medal needs every set of one of its packs, as
/// on osu!, so a pack only counts once the server has all of it.
/// </summary>
public sealed class PackMedalAwarder
{
    /// <summary>Local packs are numbered from here, clear of osu!'s pack numbers (which ppy's processor checks).</summary>
    public const int LocalPackFloor = 20000;

    private readonly OsuWebPackCompletion _completion;
    private readonly OsuWebSync _sync;
    private readonly IReadOnlyList<PackMedal> _medals;

    /// <param name="completion">Pack completion on the website's scores.</param>
    /// <param name="sync">Unlocks medals on the website.</param>
    /// <param name="medals">The pack medals (osu!'s, <see cref="PackMedalCatalog.All"/>, unless given).</param>
    public PackMedalAwarder(OsuWebPackCompletion completion, OsuWebSync sync, IReadOnlyList<PackMedal>? medals = null)
    {
        _completion = completion;
        _sync = sync;
        _medals = medals ?? PackMedalCatalog.All;
    }

    /// <summary>Checks the pack medals these new website scores could complete; returns the medals given.</summary>
    public async Task<int> CheckAsync(IReadOnlyCollection<ulong> scoreIds, CancellationToken cancellationToken = default)
    {
        var given = 0;
        foreach (var (userId, beatmapId, setId) in await _completion.PassesAsync(scoreIds, cancellationToken).ConfigureAwait(false))
        {
            var candidates = _medals.Where(m => m.Packs.Any(p => p.Sets.Contains(setId))).ToList();
            if (candidates.Count == 0)
                continue;
            var unlocked = await _completion.UnlockedAsync(userId, cancellationToken).ConfigureAwait(false);
            foreach (var medal in candidates.Where(m => !unlocked.Contains(m.Id)))
            {
                if (await CompletedAtAsync(userId, medal, cancellationToken).ConfigureAwait(false) is null)
                    continue;
                await _sync.UnlockMedalIdsAsync(userId, [medal.Id], beatmapId, cancellationToken).ConfigureAwait(false);
                given++;
            }
        }
        return given;
    }

    /// <summary>Every pack medal every player has earned, with when (for the rebuild).</summary>
    public async Task<IReadOnlyList<(int UserId, int MedalId, DateTime At)>> EarnedAsync(CancellationToken cancellationToken = default)
    {
        var earned = new List<(int, int, DateTime)>();
        foreach (var medal in _medals)
        {
            var players = await _completion.PlayersOfAsync(medal.Packs.SelectMany(p => p.Sets).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
            foreach (var userId in players)
            {
                if (await CompletedAtAsync(userId, medal, cancellationToken).ConfigureAwait(false) is { } at)
                    earned.Add((userId, medal.Id, at));
            }
        }
        return earned;
    }

    /// <summary>When the player completed any of the medal's packs (the earliest), or null.</summary>
    private async Task<DateTime?> CompletedAtAsync(int userId, PackMedal medal, CancellationToken cancellationToken)
    {
        DateTime? first = null;
        foreach (var pack in medal.Packs)
        {
            if (await _completion.CompletedAtAsync(userId, pack.Sets.ToList(), pack.Ruleset, medal.NoReductionMods, cancellationToken).ConfigureAwait(false) is { } at
                && (first is null || at < first))
            {
                first = at;
            }
        }
        return first;
    }
}
