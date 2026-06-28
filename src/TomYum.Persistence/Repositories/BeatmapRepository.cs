using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IBeatmapRepository"/> over <see cref="TomYumDbContext"/>.
/// Beatmaps are keyed by id; the <c>.osu</c> MD5 is a unique alternate key used as the wire
/// identity in both protocols. Custom (server-local) maps reserve ids at or above
/// <see cref="Beatmap.CustomIdFloor"/> per ADR-0009.
/// </summary>
public sealed class BeatmapRepository : IBeatmapRepository
{
    private readonly TomYumDbContext _db;

    /// <summary>Creates the repository over the database context.</summary>
    public BeatmapRepository(TomYumDbContext db) => _db = db;

    /// <inheritdoc />
    public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) =>
        _db.Beatmaps.AsNoTracking().FirstOrDefaultAsync(b => b.Md5 == md5, cancellationToken);

    /// <inheritdoc />
    public Task<Beatmap?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        _db.Beatmaps.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<int> ReserveCustomSetIdAsync(CancellationToken cancellationToken = default)
    {
        // The next free server-local set id: one past the current maximum custom set id, or the
        // floor itself when no custom sets exist yet. Official maps (ids below the floor) are ignored.
        var maxCustom = await _db.Beatmaps
            .Where(b => b.SetId >= Beatmap.CustomIdFloor)
            .Select(b => (int?)b.SetId)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        return maxCustom is null ? Beatmap.CustomIdFloor : maxCustom.Value + 1;
    }
}
