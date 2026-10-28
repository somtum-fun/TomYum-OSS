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

        var tracked = _db.StaffLog.Add(entry);
        // Text that doesn't fit its column is cut (with "…") rather than failing the save: a summary can carry
        // what a client reported (an anti-cheat plugin's module list, tablet filters), and a failed save here used to fail the
        // whole request that logged it (a score submission, a restriction).
        Fit(tracked.Property(nameof(StaffLogEntry.Action)), ActionLength);
        Fit(tracked.Property(nameof(StaffLogEntry.Summary)), SummaryLength);
        Fit(tracked.Property(nameof(StaffLogEntry.Result)), ResultLength);
        Fit(tracked.Property(nameof(StaffLogEntry.Via)), ViaLength);
        Fit(tracked.Property(nameof(StaffLogEntry.JobId)), JobIdLength);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.Entry(entry).State = EntityState.Detached;
    }

    // The columns' lengths (StaffLogEntryConfiguration).
    private const int ActionLength = 64;
    private const int SummaryLength = 512;
    private const int ResultLength = 32;
    private const int ViaLength = 16;
    private const int JobIdLength = 32;

    private static void Fit(Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry property, int length)
    {
        if (property.CurrentValue is string text && text.Length > length)
            property.CurrentValue = string.Concat(text.AsSpan(0, length - 1), "…");
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
        if (query.ActorId is { } actorId)
            entries = entries.Where(e => e.ActorId == actorId);
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
