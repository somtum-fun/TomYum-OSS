using TomYum.Domain.Entities;

namespace TomYum.Domain.Repositories;

/// <summary>Custom beatmap sets' owners and the difficulty ids handed out for them.</summary>
public interface ICustomBeatmapSetRepository
{
    /// <summary>The set, or null.</summary>
    Task<CustomBeatmapSet?> GetAsync(int setId, CancellationToken cancellationToken = default);

    /// <summary>The difficulty ids that belong to the set.</summary>
    Task<IReadOnlyList<int>> GetSlotsAsync(int setId, CancellationToken cancellationToken = default);

    /// <summary>The user's sets.</summary>
    Task<IReadOnlyList<CustomBeatmapSet>> ListByOwnerAsync(int ownerId, CancellationToken cancellationToken = default);

    /// <summary>Stores the set (new or changed) with exactly <paramref name="slots"/> as its difficulty ids.</summary>
    Task SaveAsync(CustomBeatmapSet set, IReadOnlyCollection<int> slots, CancellationToken cancellationToken = default);
}
