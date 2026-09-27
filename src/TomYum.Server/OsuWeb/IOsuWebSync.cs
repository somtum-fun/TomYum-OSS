using TomYum.Domain.Entities;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// Keeps the osu-web website in step with gameplay (docs/osu-web.md). A no-op unless accounts live
/// in osu-web (<c>Database:OsuWebName</c>).
/// </summary>
public interface IOsuWebSync
{
    /// <summary>Called after a score and the player's TomYum stats have been saved.</summary>
    Task ScoreSubmittedAsync(Score score, CancellationToken cancellationToken = default);
}

/// <summary>Standalone TomYum: nothing to sync.</summary>
public sealed class NoOsuWebSync : IOsuWebSync
{
    public Task ScoreSubmittedAsync(Score score, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
