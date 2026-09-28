using System.Collections.Concurrent;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Server.Bancho;

/// <summary>Throwaway in-memory login hashes used when no database is configured (local dev, tests).</summary>
public sealed class InMemoryClientHashRepository : IClientHashRepository
{
    private readonly ConcurrentDictionary<(int UserId, string OsuPath, string Adapters, string UninstallId, string DiskSerial), ClientHash> _hashes = new();
    private readonly ConcurrentDictionary<int, VerifiedUser> _verified = new();

    public Task RecordAsync(ClientHash hashes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hashes);

        _hashes.AddOrUpdate(
            (hashes.UserId, hashes.OsuPath, hashes.Adapters, hashes.UninstallId, hashes.DiskSerial),
            _ => new ClientHash
            {
                UserId = hashes.UserId, OsuPath = hashes.OsuPath, Adapters = hashes.Adapters,
                UninstallId = hashes.UninstallId, DiskSerial = hashes.DiskSerial,
                LatestTime = hashes.LatestTime, Occurrences = 1,
            },
            (_, existing) => new ClientHash
            {
                UserId = existing.UserId, OsuPath = existing.OsuPath, Adapters = existing.Adapters,
                UninstallId = existing.UninstallId, DiskSerial = existing.DiskSerial,
                LatestTime = hashes.LatestTime, Occurrences = existing.Occurrences + 1,
            });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<int>> FindOtherUsersAsync(int userId, ClientHashQuery query, int limit = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        IReadOnlyList<int> users = _hashes.Values
            .Where(h => h.UserId != userId
                && ((query.Adapters != null && h.Adapters == query.Adapters)
                    || (query.UninstallId != null && h.UninstallId == query.UninstallId)
                    || (query.DiskSerial != null && h.DiskSerial == query.DiskSerial)))
            .GroupBy(h => h.UserId)
            .OrderByDescending(g => g.Max(h => h.LatestTime))
            .Select(g => g.Key)
            .Take(Math.Clamp(limit, 1, 500))
            .ToList();
        return Task.FromResult(users);
    }

    public Task<IReadOnlyList<ClientHash>> ListAsync(int userId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ClientHash> hashes = _hashes.Values.Where(h => h.UserId == userId).OrderByDescending(h => h.LatestTime).ToList();
        return Task.FromResult(hashes);
    }

    public Task<bool> IsVerifiedAsync(int userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_verified.ContainsKey(userId));

    public Task<bool> VerifyAsync(int userId, int verifiedBy, DateTime at, CancellationToken cancellationToken = default) =>
        Task.FromResult(_verified.TryAdd(userId, new VerifiedUser { UserId = userId, VerifiedAt = at, VerifiedBy = verifiedBy }));
}
