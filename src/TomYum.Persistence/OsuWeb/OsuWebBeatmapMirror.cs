using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// Copies TomYum's beatmaps into osu-web's <c>osu_beatmapsets</c>/<c>osu_beatmaps</c> so the website
/// can show them. TomYum's own <c>beatmaps</c> table stays the working copy the server and tools
/// read; this is a one-way mirror (docs/osu-web.md).
/// </summary>
/// <remarks>
/// The server imports a whole set the first time one of its maps is played. Maps that came from the
/// osu! API carry <see cref="Beatmap.Details"/> (AR/OD/CS/HP, BPM, tags, …), which fill the matching
/// columns; without them those columns keep osu-web's defaults and <c>hit_length</c> is the total
/// length.
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

        if (beatmap.Details?.PackTags is { } packTags)
            await SyncPacksAsync(beatmap.SetId, packTags, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts set <paramref name="setId"/> in exactly the packs <paramref name="tags"/> (creating packs
    /// the website doesn't have yet; the osu! API only gives their tags, which then serve as name).
    /// </summary>
    public async Task SyncPacksAsync(int setId, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var packs = await _db.BeatmapPacks.Where(p => tags.Contains(p.Tag)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var tag in tags.Where(t => packs.All(p => p.Tag != t)))
        {
            var pack = new OsuWebBeatmapPack { Tag = tag, Name = OsuWebBeatmaps.PackName(tag), Date = DateTime.UtcNow };
            _db.BeatmapPacks.Add(pack);
            packs.Add(pack);
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var set = (uint)setId;
        var wanted = packs.Select(p => (ushort)p.PackId).ToHashSet();
        var current = await _db.BeatmapPackItems.Where(i => i.BeatmapsetId == set).ToListAsync(cancellationToken).ConfigureAwait(false);
        _db.BeatmapPackItems.RemoveRange(current.Where(i => !wanted.Contains(i.PackId)));
        foreach (var packId in wanted.Where(id => current.All(i => i.PackId != id)))
            _db.BeatmapPackItems.Add(new OsuWebBeatmapPackItem { PackId = packId, BeatmapsetId = set });

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

    /// <summary>
    /// Sets a set's <c>approved</c> from its difficulties after they were changed one by one (staff
    /// status changes): the "best" status any difficulty has, in osu!'s order ranked, approved,
    /// loved, qualified, pending, WIP, graveyard. Gives it a ranked date when it gets one.
    /// </summary>
    public async Task SyncSetStatusAsync(int setId, CancellationToken cancellationToken = default)
    {
        var set = await _db.Beatmapsets.FirstOrDefaultAsync(s => s.BeatmapsetId == (uint)setId, cancellationToken).ConfigureAwait(false);
        if (set is null)
            return;

        var statuses = await _db.Beatmaps.AsNoTracking()
            .Where(b => b.BeatmapsetId == (uint)setId)
            .Select(b => b.Approved)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (statuses.Count == 0)
            return;

        var best = OsuWebBeatmaps.SetStatusFor(statuses);
        if (best != set.Approved)
        {
            set.Approved = best;
            set.ApprovedDate = OsuWebBeatmaps.HasApprovedDate(best) ? set.ApprovedDate ?? DateTime.UtcNow : null;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        _db.ChangeTracker.Clear();
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
        // Official sets have a cover on ppy's asset server; custom ones only once one is uploaded.
        if (!beatmap.IsCustom)
            set.CoverUpdatedAt ??= set.LastUpdate;

        var map = existingMap;
        if (map is null)
        {
            map = new OsuWebBeatmap { BeatmapId = (uint)beatmap.Id };
            _db.Beatmaps.Add(map);
        }
        map.BeatmapsetId = (uint)beatmap.SetId;
        map.Checksum = beatmap.Md5;
        if (!string.IsNullOrEmpty(beatmap.Artist) || !string.IsNullOrEmpty(beatmap.Title))
            map.Filename = OsuWebBeatmaps.Truncate(TomYum.Domain.Services.OsuFileName.For(beatmap.Artist, beatmap.Title, beatmap.Creator, beatmap.Version), 150);
        map.Version = OsuWebBeatmaps.Truncate(beatmap.Version);
        map.TotalLength = (uint)Math.Max(0, beatmap.TotalLength);
        map.HitLength = map.TotalLength;
        map.Playmode = (byte)Math.Clamp(beatmap.Mode, 0, 3);
        map.Approved = approved;
        map.LastUpdate = lastUpdate;
        map.Difficultyrating = (float)beatmap.StarRating;
        map.MaxCombo = (uint)Math.Max(0, beatmap.MaxCombo);

        if (beatmap.Details is { } details)
            ApplyDetails(details, set, map);
        return true;
    }

    /// <summary>
    /// Copies the website-only metadata. Only values the API actually gave overwrite a column, so a
    /// later source with less data (the keyless mirror has no object counts) never blanks one.
    /// </summary>
    private static void ApplyDetails(BeatmapDetails details, OsuWebBeatmapset set, OsuWebBeatmap map)
    {
        map.DiffSize = Math.Max(0, details.CircleSize ?? map.DiffSize);
        map.DiffApproach = Math.Max(0, details.ApproachRate ?? map.DiffApproach);
        map.DiffOverall = Math.Max(0, details.OverallDifficulty ?? map.DiffOverall);
        map.DiffDrain = Math.Max(0, details.DrainRate ?? map.DiffDrain);
        map.Bpm = details.Bpm ?? map.Bpm;
        if (details.HitLength is { } hitLength)
            map.HitLength = (uint)Math.Max(0, hitLength);
        if (details is { CountCircles: { } circles, CountSliders: { } sliders, CountSpinners: { } spinners })
        {
            map.CountNormal = (uint)Math.Max(0, circles);
            map.CountSlider = (uint)Math.Max(0, sliders);
            map.CountSpinner = (uint)Math.Max(0, spinners);
            map.CountTotal = map.CountNormal + map.CountSlider + map.CountSpinner;
        }

        set.Bpm = details.Bpm ?? set.Bpm;
        set.GenreId = (ushort)Math.Clamp(details.GenreId ?? set.GenreId, 1, ushort.MaxValue);
        set.LanguageId = (ushort)Math.Clamp(details.LanguageId ?? set.LanguageId, 1, ushort.MaxValue);
        if (details.Source is { } source)
            set.Source = OsuWebBeatmaps.Truncate(source, 200);
        if (details.Tags is { } tags)
            set.Tags = OsuWebBeatmaps.Truncate(tags, 1000);
        set.Video = details.Video ?? set.Video;
        set.Storyboard = details.Storyboard ?? set.Storyboard;
        if (details.ApprovedDate is { } approvedDate && set.ApprovedDate is not null)
            set.ApprovedDate = OsuWebBeatmaps.ToTimestamp(approvedDate, set.ApprovedDate.Value);
    }
}
