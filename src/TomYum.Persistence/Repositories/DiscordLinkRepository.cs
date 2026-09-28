using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Persistence.Repositories;

public sealed class DiscordLinkRepository : IDiscordLinkRepository
{
    private readonly TomYumDbContext _db;

    public DiscordLinkRepository(TomYumDbContext db) => _db = db;

    public Task<DiscordLink?> GetByDiscordIdAsync(ulong discordId, CancellationToken cancellationToken = default) =>
        _db.DiscordLinks.AsNoTracking().FirstOrDefaultAsync(l => l.DiscordId == discordId, cancellationToken);

    public Task<DiscordLink?> GetByUserIdAsync(int userId, CancellationToken cancellationToken = default) =>
        _db.DiscordLinks.AsNoTracking().FirstOrDefaultAsync(l => l.UserId == userId, cancellationToken);

    public async Task LinkAsync(int userId, ulong discordId, DateTime at, CancellationToken cancellationToken = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await _db.DiscordLinks.Where(l => l.UserId == userId || l.DiscordId == discordId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            _db.DiscordLinks.Add(new DiscordLink { UserId = userId, DiscordId = discordId, LinkedAt = at });
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
        }).ConfigureAwait(false);
    }

    public async Task<bool> UnlinkAsync(ulong discordId, CancellationToken cancellationToken = default) =>
        await _db.DiscordLinks.Where(l => l.DiscordId == discordId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;

    public async Task<IReadOnlyList<DiscordLink>> ListAsync(CancellationToken cancellationToken = default) =>
        await _db.DiscordLinks.AsNoTracking().OrderBy(l => l.UserId).ToListAsync(cancellationToken).ConfigureAwait(false);
}
