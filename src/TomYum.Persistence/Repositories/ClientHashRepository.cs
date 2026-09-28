using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Persistence.Repositories;

public sealed class ClientHashRepository : IClientHashRepository
{
    private readonly TomYumDbContext _db;

    public ClientHashRepository(TomYumDbContext db) => _db = db;

    public async Task RecordAsync(ClientHash hashes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hashes);

        // One statement, so two logins at once can't both insert the same combination.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO client_hashes (user_id, osupath, adapters, uninstall_id, disk_serial, latest_time, occurrences)
            VALUES ({hashes.UserId}, {hashes.OsuPath}, {hashes.Adapters}, {hashes.UninstallId}, {hashes.DiskSerial}, {hashes.LatestTime}, 1)
            ON DUPLICATE KEY UPDATE occurrences = occurrences + 1, latest_time = VALUES(latest_time)
            """,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<int>> FindOtherUsersAsync(int userId, ClientHashQuery query, int limit = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.IsEmpty)
            return [];

        var adapters = query.Adapters;
        var uninstall = query.UninstallId;
        var disk = query.DiskSerial;
        return await _db.ClientHashes.AsNoTracking()
            .Where(h => h.UserId != userId
                && ((adapters != null && h.Adapters == adapters)
                    || (uninstall != null && h.UninstallId == uninstall)
                    || (disk != null && h.DiskSerial == disk)))
            .GroupBy(h => h.UserId)
            .OrderByDescending(g => g.Max(h => h.LatestTime))
            .Select(g => g.Key)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ClientHash>> ListAsync(int userId, CancellationToken cancellationToken = default) =>
        await _db.ClientHashes.AsNoTracking()
            .Where(h => h.UserId == userId)
            .OrderByDescending(h => h.LatestTime)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<bool> IsVerifiedAsync(int userId, CancellationToken cancellationToken = default) =>
        _db.VerifiedUsers.AnyAsync(v => v.UserId == userId, cancellationToken);

    public async Task<bool> VerifyAsync(int userId, int verifiedBy, DateTime at, CancellationToken cancellationToken = default)
    {
        var added = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT IGNORE INTO verified_users (user_id, verified_at, verified_by) VALUES ({userId}, {at}, {verifiedBy})",
            cancellationToken).ConfigureAwait(false);
        return added > 0;
    }
}
