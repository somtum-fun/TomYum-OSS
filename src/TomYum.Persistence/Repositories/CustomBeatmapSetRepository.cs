using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Persistence.Repositories;

/// <summary>EF Core implementation of <see cref="ICustomBeatmapSetRepository"/>.</summary>
public sealed class CustomBeatmapSetRepository : ICustomBeatmapSetRepository
{
    private readonly TomYumDbContext _db;

    public CustomBeatmapSetRepository(TomYumDbContext db) => _db = db;

    public Task<CustomBeatmapSet?> GetAsync(int setId, CancellationToken cancellationToken = default) =>
        _db.CustomBeatmapSets.AsNoTracking().FirstOrDefaultAsync(s => s.SetId == setId, cancellationToken);

    public async Task<IReadOnlyList<int>> GetSlotsAsync(int setId, CancellationToken cancellationToken = default) =>
        await _db.CustomBeatmapSlots.AsNoTracking()
            .Where(s => s.SetId == setId)
            .OrderBy(s => s.BeatmapId)
            .Select(s => s.BeatmapId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<CustomBeatmapSet>> ListByOwnerAsync(int ownerId, CancellationToken cancellationToken = default) =>
        await _db.CustomBeatmapSets.AsNoTracking()
            .Where(s => s.OwnerId == ownerId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task SaveAsync(CustomBeatmapSet set, IReadOnlyCollection<int> slots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(slots);

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var existing = await _db.CustomBeatmapSets.FirstOrDefaultAsync(s => s.SetId == set.SetId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                _db.CustomBeatmapSets.Add(new CustomBeatmapSet
                {
                    SetId = set.SetId,
                    OwnerId = set.OwnerId,
                    Target = set.Target,
                    CreatedAt = set.CreatedAt,
                    UpdatedAt = set.UpdatedAt,
                    UploadedAt = set.UploadedAt,
                });
            }
            else
            {
                existing.OwnerId = set.OwnerId;
                existing.Target = set.Target;
                existing.UpdatedAt = set.UpdatedAt;
                existing.UploadedAt = set.UploadedAt;
            }

            var current = await _db.CustomBeatmapSlots.Where(s => s.SetId == set.SetId).ToListAsync(cancellationToken).ConfigureAwait(false);
            _db.CustomBeatmapSlots.RemoveRange(current.Where(s => !slots.Contains(s.BeatmapId)));
            foreach (var id in slots.Where(id => current.All(s => s.BeatmapId != id)))
                _db.CustomBeatmapSlots.Add(new CustomBeatmapSlot { BeatmapId = id, SetId = set.SetId });

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
        }).ConfigureAwait(false);
    }
}
