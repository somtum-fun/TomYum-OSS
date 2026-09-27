using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// Copies TomYum's beatmaps into osu-web's <c>osu_beatmapsets</c>/<c>osu_beatmaps</c> so the website
/// can show them. TomYum's own <c>beatmaps</c> table stays the working copy the server and tools
/// read; this is a one-way mirror (docs/osu-web.md).
/// </summary>
/// <remarks>
/// TomYum only knows the difficulties that were played, so a set on the site lists those. Columns
/// TomYum has no data for keep osu-web's defaults (bpm, AR/OD/CS/HP, object counts, tags, …);
/// <c>hit_length</c> is filled with the total length.
/// </remarks>
public sealed class OsuWebBeatmapMirror
{
    private readonly OsuWebDbContext _db;

    public OsuWebBeatmapMirror(OsuWebDbContext db) => _db = db;

    /// <summary>Inserts or updates <paramref name="beatmap"/> and its set on osu-web.</summary>
    public async Task WriteAsync(Beatmap beatmap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beatmap);

        if (!Apply(beatmap, await LoadSetAsync(beatmap, cancellationToken).ConfigureAwait(false),
                await _db.Beatmaps.FirstOrDefaultAsync(b => b.BeatmapId == (uint)beatmap.Id, cancellationToken).ConfigureAwait(false)))
        {
            return;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Mirrors every beatmap in <paramref name="beatmaps"/> that osu-web doesn't have yet (existing
    /// rows are left alone). Returns how many were added. Used at startup for maps played before
    /// the osu-web link existed.
    /// </summary>
    public async Task<int> BackfillAsync(IReadOnlyCollection<Beatmap> beatmaps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beatmaps);

        var existing = (await _db.Beatmaps.AsNoTracking().Select(b => b.BeatmapId)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet();
        var missing = beatmaps
            .Where(b => b.Id is > 0 and <= OsuWebBeatmaps.MaxId && !existing.Contains((uint)b.Id))
            .DistinctBy(b => b.Id)
            .ToList();

        var added = 0;
        foreach (var batch in missing.Chunk(500))
        {
            foreach (var beatmap in batch)
            {
                if (Apply(beatmap, await LoadSetAsync(beatmap, cancellationToken).ConfigureAwait(false), existingMap: null))
                    added++;
            }
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
        }
        return added;
    }

    private async Task<OsuWebBeatmapset?> LoadSetAsync(Beatmap beatmap, CancellationToken cancellationToken)
    {
        var setId = (uint)beatmap.SetId;
        // Several maps of one set can be pending in the same batch: prefer the tracked instance.
        return _db.Beatmapsets.Local.FirstOrDefault(s => s.BeatmapsetId == setId)
            ?? await _db.Beatmapsets.FirstOrDefaultAsync(s => s.BeatmapsetId == setId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stages the set + map rows for <paramref name="beatmap"/>; false when it isn't mirrored.</summary>
    private bool Apply(Beatmap beatmap, OsuWebBeatmapset? set, OsuWebBeatmap? existingMap)
    {
        if (beatmap.Id is <= 0 or > OsuWebBeatmaps.MaxId || beatmap.SetId is <= 0 or > OsuWebBeatmaps.MaxId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(beatmap),
                $"Beatmap {beatmap.Id} (set {beatmap.SetId}) is outside osu-web's id range 1-{OsuWebBeatmaps.MaxId}.");
        }

        if (OsuWebBeatmaps.ToApproved(beatmap) is not { } approved)
            return false;

        var now = DateTime.UtcNow;
        var lastUpdate = OsuWebBeatmaps.ToTimestamp(beatmap.LastUpdate, now);

        if (set is null)
        {
            set = new OsuWebBeatmapset { BeatmapsetId = (uint)beatmap.SetId, SubmitDate = lastUpdate };
            _db.Beatmapsets.Add(set);
        }
        set.Artist = OsuWebBeatmaps.Truncate(beatmap.Artist);
        set.Title = OsuWebBeatmaps.Truncate(beatmap.Title);
        set.Creator = OsuWebBeatmaps.Truncate(beatmap.Creator);
        set.Approved = approved;
        set.ApprovedDate = OsuWebBeatmaps.HasApprovedDate(approved) ? set.ApprovedDate ?? lastUpdate : null;
        if (lastUpdate > set.LastUpdate)
            set.LastUpdate = lastUpdate;

        var map = existingMap;
        if (map is null)
        {
            map = new OsuWebBeatmap { BeatmapId = (uint)beatmap.Id };
            _db.Beatmaps.Add(map);
        }
        map.BeatmapsetId = (uint)beatmap.SetId;
        map.Checksum = beatmap.Md5;
        map.Version = OsuWebBeatmaps.Truncate(beatmap.Version);
        map.TotalLength = (uint)Math.Max(0, beatmap.TotalLength);
        map.HitLength = map.TotalLength;
        map.Playmode = (byte)Math.Clamp(beatmap.Mode, 0, 3);
        map.Approved = approved;
        map.LastUpdate = lastUpdate;
        map.Difficultyrating = (float)beatmap.StarRating;
        map.MaxCombo = (uint)Math.Max(0, beatmap.MaxCombo);
        return true;
    }
}
