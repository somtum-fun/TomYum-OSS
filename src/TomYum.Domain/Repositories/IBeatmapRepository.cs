using TomYum.Domain.Entities;

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

    /// <summary>
    /// Reserves and returns the next available server-local beatmap set id
    /// (≥ <see cref="Beatmap.CustomIdFloor"/>) for custom submission.
    /// </summary>
    Task<int> ReserveCustomSetIdAsync(CancellationToken cancellationToken = default);
}
