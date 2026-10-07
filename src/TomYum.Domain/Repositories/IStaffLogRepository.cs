using TomYum.Domain.Entities;

namespace TomYum.Domain.Repositories;

/// <summary>Filter for <see cref="IStaffLogRepository.ListAsync"/>; unset fields don't filter.</summary>
/// <param name="Action">Exact action, or a prefix ending in <c>.</c> (e.g. <c>user.</c>).</param>
/// <param name="TargetId">Only entries about this target.</param>
/// <param name="JobId">Only entries from this bulk job.</param>
/// <param name="BeforeId">Paging: only entries older than this id.</param>
/// <param name="Limit">Maximum entries (capped at 500).</param>
/// <param name="ActorId">Only entries by this staff member (or bot).</param>
public sealed record StaffLogQuery(string? Action = null, long? TargetId = null, string? JobId = null, long? BeforeId = null, int Limit = 50, long? ActorId = null);

/// <summary>The staff action log (<see cref="StaffLogEntry"/>).</summary>
public interface IStaffLogRepository
{
    Task AddAsync(StaffLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Matching entries, newest first.</summary>
    Task<IReadOnlyList<StaffLogEntry>> ListAsync(StaffLogQuery query, CancellationToken cancellationToken = default);
}
