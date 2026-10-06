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

    /// <summary>
    /// The website's sign-up rules for stable's in-game registration (IP bans, one account per network a
    /// day, no VPN, proxy or hosting network): null when the sign-up from <paramref name="ip"/> may go
    /// ahead, else the message for the player. <paramref name="quick"/> is the keystroke check (the cheap
    /// rules only). When the website can't be asked, a quick check passes and a final one is refused.
    /// </summary>
    Task<string?> CheckRegistrationAsync(string? ip, string username, string? country, bool quick, CancellationToken cancellationToken = default);

    /// <summary>Called after an account was created in-game from <paramref name="ip"/> (for the website's sign-up rules).</summary>
    Task RegistrationCreatedAsync(int userId, string username, string? ip, string? country, CancellationToken cancellationToken = default);
}

/// <summary>Standalone TomYum: nothing to sync.</summary>
public sealed class NoOsuWebSync : IOsuWebSync
{
    public Task<OsuWebPerformance?> ScoreSubmittedAsync(Score score, CancellationToken cancellationToken = default) =>
        Task.FromResult<OsuWebPerformance?>(null);

    public Task MedalsUnlockedAsync(int userId, IReadOnlyList<Medal> medals, int beatmapId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task AccountCreatedAsync(int userId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string?> CheckRegistrationAsync(string? ip, string username, string? country, bool quick, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task RegistrationCreatedAsync(int userId, string username, string? ip, string? country, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
