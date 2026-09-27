using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Persistence.Repositories;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// <see cref="IBeatmapRepository"/> for osu-web mode: TomYum's own table stays the source of truth
/// (reads, frozen statuses, tools like RecalcPp), and every write is mirrored to osu-web so the
/// website shows the map.
/// </summary>
public sealed class OsuWebMirroringBeatmapRepository : IBeatmapRepository
{
    private readonly BeatmapRepository _inner;
    private readonly OsuWebBeatmapMirror _mirror;
    private readonly OsuWebDbContext _osuWeb;

    public OsuWebMirroringBeatmapRepository(BeatmapRepository inner, OsuWebBeatmapMirror mirror, OsuWebDbContext osuWeb)
    {
        _inner = inner;
        _mirror = mirror;
        _osuWeb = osuWeb;
    }

    public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) =>
        _inner.GetByMd5Async(md5, cancellationToken);

    public Task<Beatmap?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        _inner.GetByIdAsync(id, cancellationToken);

    public async Task UpsertAsync(Beatmap beatmap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beatmap);

        await _inner.UpsertAsync(beatmap, cancellationToken).ConfigureAwait(false);

        // Mirror what TomYum actually stored: a frozen status on the existing row beats the API's.
        var effective = await _inner.GetByIdAsync(beatmap.Id, cancellationToken).ConfigureAwait(false) ?? beatmap;
        if (effective.Status == beatmap.Status)
            effective.OsuApiStatus = beatmap.OsuApiStatus;
        effective.Details = beatmap.Details;

        await _mirror.WriteAsync(effective, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ReserveCustomSetIdAsync(CancellationToken cancellationToken = default)
    {
        // Custom ids must be free on both sides (a set could exist on osu-web only).
        var local = await _inner.ReserveCustomSetIdAsync(cancellationToken).ConfigureAwait(false);
        var floor = (uint)Beatmap.CustomIdFloor;
        var maxOnOsuWeb = await _osuWeb.Beatmapsets.AsNoTracking()
            .Where(s => s.BeatmapsetId >= floor)
            .Select(s => (uint?)s.BeatmapsetId)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

        var next = Math.Max(local, maxOnOsuWeb is null ? Beatmap.CustomIdFloor : (int)maxOnOsuWeb.Value + 1);
        if (next > OsuWebBeatmaps.MaxId)
            throw new InvalidOperationException($"Custom beatmap set ids are exhausted (osu-web's limit is {OsuWebBeatmaps.MaxId}).");
        return next;
    }
}
