using TomYum.Domain.Entities;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Achievements;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// Keeps the osu-web website in step with gameplay (docs/osu-web.md). A no-op unless accounts live
/// in osu-web (<c>Database:OsuWebName</c>).
/// </summary>
public interface IOsuWebSync
{
    /// <summary>
    /// Called after a score and the player's TomYum stats have been saved. Returns the player's
    /// combined stable + lazer pp when ppy's score processor owns it (TomYum's stats then hold it
    /// too), else null.
    /// </summary>
    Task<OsuWebPerformance?> ScoreSubmittedAsync(Score score, CancellationToken cancellationToken = default);

    /// <summary>Called after TomYum recorded new medal unlocks (earned on <paramref name="beatmapId"/>).</summary>
    Task MedalsUnlockedAsync(int userId, IReadOnlyList<Medal> medals, int beatmapId, CancellationToken cancellationToken = default);

    /// <summary>Called after an account was created in-game.</summary>
    Task AccountCreatedAsync(int userId, CancellationToken cancellationToken = default);
}

/// <summary>Standalone TomYum: nothing to sync.</summary>
public sealed class NoOsuWebSync : IOsuWebSync
{
    public Task<OsuWebPerformance?> ScoreSubmittedAsync(Score score, CancellationToken cancellationToken = default) =>
        Task.FromResult<OsuWebPerformance?>(null);

    public Task MedalsUnlockedAsync(int userId, IReadOnlyList<Medal> medals, int beatmapId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task AccountCreatedAsync(int userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
