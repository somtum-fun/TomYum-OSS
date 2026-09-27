using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
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
    public async Task<IReadOnlyList<Beatmap>> GetBySetAsync(int setId, CancellationToken cancellationToken = default) =>
        await _db.Beatmaps.AsNoTracking()
            .Where(b => b.SetId == setId)
            .OrderBy(b => b.Mode).ThenBy(b => b.StarRating)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> SetStatusAsync(int beatmapId, RankedStatus status, CancellationToken cancellationToken = default)
    {
        var existing = await _db.Beatmaps
            .FirstOrDefaultAsync(b => b.Id == beatmapId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
            return false;

        existing.Status = status;
        existing.StatusFrozen = true;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.Entry(existing).State = EntityState.Detached;
        return true;
    }

    /// <inheritdoc />
    public async Task UpsertAsync(Beatmap beatmap, CancellationToken cancellationToken = default)
    {
        var existing = await _db.Beatmaps
            .FirstOrDefaultAsync(b => b.Id == beatmap.Id, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _db.Beatmaps.Add(beatmap);
        }
        else
        {
            existing.SetId      = beatmap.SetId;
            existing.Md5        = beatmap.Md5;
            existing.Artist     = beatmap.Artist;
            existing.Title      = beatmap.Title;
            existing.Version    = beatmap.Version;
            existing.Creator    = beatmap.Creator;
            existing.Mode       = beatmap.Mode;
            existing.StarRating = beatmap.StarRating;
            existing.TotalLength = beatmap.TotalLength;
            existing.MaxCombo   = beatmap.MaxCombo;
            existing.LastUpdate = beatmap.LastUpdate;
            if (!existing.StatusFrozen)
                existing.Status = beatmap.Status;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

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
