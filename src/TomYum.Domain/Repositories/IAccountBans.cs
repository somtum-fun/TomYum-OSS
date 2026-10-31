namespace TomYum.Domain.Repositories;

/// <summary>
/// Bans set from the admin panel: a banned account is restricted, can't log in anywhere, and neither
/// can any other account seen on its hardware (staff and anti-cheat whitelisted accounts excepted).
/// </summary>
public interface IAccountBans
{
    /// <summary>Whether the account is banned.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> IsBannedAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>The first banned account among <paramref name="userIds"/>, or null when none is.</summary>
    /// <param name="userIds">The accounts to look through.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int?> FirstBannedAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken = default);

    /// <summary>Whether staff put the account on the anti-cheat whitelist (it may share a banned machine).</summary>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> IsWhitelistedAsync(int userId, CancellationToken cancellationToken = default);
}
