using TomYum.Domain.Repositories;

namespace TomYum.Server.Tests.Bancho;

/// <summary>The admin panel's bans, in memory.</summary>
internal sealed class BannedAccounts : IAccountBans
{
    public BannedAccounts(params int[] banned) => Banned = [.. banned];

    public HashSet<int> Banned { get; }

    public HashSet<int> Whitelisted { get; } = [];

    public Task<bool> IsBannedAsync(int userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Banned.Contains(userId));

    public Task<int?> FirstBannedAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<int?>(userIds.Where(Banned.Contains).Select(id => (int?)id).FirstOrDefault());

    public Task<bool> IsWhitelistedAsync(int userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Whitelisted.Contains(userId));
}
