using TomYum.Domain.Entities;
using TomYum.Domain.Enums;

namespace TomYum.Domain.Repositories;

/// <summary>
/// Persistence port for <see cref="Beatmap"/> entities. Implemented in
/// <c>TomYum.Persistence</c>. See ADR-0001 and ADR-0009.
/// </summary>
public interface IBeatmapRepository
{
    /// <summary>Loads a beatmap by its MD5 hash (the wire identity), or <c>null</c>.</summary>
    Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default);

    /// <summary>Loads a beatmap by id, or <c>null</c>.</summary>
    Task<Beatmap?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Every stored difficulty of set <paramref name="setId"/>.</summary>
    Task<IReadOnlyList<Beatmap>> GetBySetAsync(int setId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a staff-chosen <paramref name="status"/> on an existing beatmap and freezes it
    /// (<see cref="Beatmap.StatusFrozen"/>), so later osu! API refreshes keep it. False when the
    /// beatmap isn't stored.
    /// </summary>
    Task<bool> SetStatusAsync(int beatmapId, RankedStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops a staff-chosen status (clears <see cref="Beatmap.StatusFrozen"/>), so the next
    /// <see cref="UpsertAsync"/> takes the osu! API's status again. False when the beatmap isn't stored.
    /// </summary>
    Task<bool> UnfreezeStatusAsync(int beatmapId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the beatmap if it doesn't exist; updates all metadata fields if it does.
    /// Respects <see cref="Beatmap.StatusFrozen"/>: when the existing row is frozen,
    /// the <see cref="Beatmap.Status"/> field is left unchanged.
    /// </summary>
    Task UpsertAsync(Beatmap beatmap, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reserves and returns the next available server-local beatmap set id
    /// (≥ <see cref="Beatmap.CustomIdFloor"/>) for custom submission.
    /// </summary>
    Task<int> ReserveCustomSetIdAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The first free server-local beatmap (difficulty) id (≥ <see cref="Beatmap.CustomIdFloor"/>);
    /// every id above it is free too, so a set's new difficulties take consecutive ids from it.
    /// </summary>
    Task<int> ReserveCustomBeatmapIdAsync(CancellationToken cancellationToken = default);
}
