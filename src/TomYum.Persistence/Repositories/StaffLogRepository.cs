using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Persistence.Repositories;

public sealed class StaffLogRepository : IStaffLogRepository
{
    private const int MaxLimit = 500;

    private readonly TomYumDbContext _db;

    public StaffLogRepository(TomYumDbContext db) => _db = db;

    public async Task AddAsync(StaffLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _db.StaffLog.Add(entry);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.Entry(entry).State = EntityState.Detached;
    }

    public async Task<IReadOnlyList<StaffLogEntry>> ListAsync(StaffLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var entries = _db.StaffLog.AsNoTracking();
        if (!string.IsNullOrEmpty(query.Action))
        {
            entries = query.Action.EndsWith('.')
                ? entries.Where(e => e.Action.StartsWith(query.Action))
                : entries.Where(e => e.Action == query.Action);
        }
        if (query.TargetId is { } targetId)
            entries = entries.Where(e => e.TargetId == targetId);
        if (!string.IsNullOrEmpty(query.JobId))
            entries = entries.Where(e => e.JobId == query.JobId);
        if (query.BeforeId is { } beforeId)
            entries = entries.Where(e => e.Id < beforeId);

        return await entries
            .OrderByDescending(e => e.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxLimit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
