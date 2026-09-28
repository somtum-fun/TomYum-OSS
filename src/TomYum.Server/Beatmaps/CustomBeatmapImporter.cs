using System.Globalization;
using System.IO.Compression;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Performance;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Beatmaps;

/// <summary>An upload that can't be taken, with the reason shown to the uploader.</summary>
public sealed class CustomUploadException(string message) : Exception(message);

/// <summary>A difficulty of an uploaded set.</summary>
public sealed record CustomUploadDifficulty(int BeatmapId, string Version, int Mode, double StarRating, string Md5);

/// <summary>What <see cref="CustomBeatmapImporter.ImportAsync"/> stored.</summary>
public sealed record CustomUploadResult(int SetId, string Artist, string Title, string Creator, int OwnerId, bool Updated, IReadOnlyList<CustomUploadDifficulty> Difficulties);

/// <summary>
/// Turns an uploaded <c>.osz</c> into a custom beatmap set (ids from <see cref="Beatmap.CustomIdFloor"/>):
/// every difficulty gets its own id, written into the file (so the client and the website agree on
/// it), and the set is stored for download (<see cref="CustomBeatmapStore"/>), its <c>.osu</c> files
/// kept for pp, its maps saved as pending (mirrored to the website) and indexed for the listing. An
/// update of an existing custom set keeps the ids of difficulties with the same name.
/// </summary>
public sealed class CustomBeatmapImporter
{
    /// <summary>Staff log action for uploads.</summary>
    public const string LogAction = "beatmapset.upload";

    /// <summary>Largest <c>.osz</c> taken (osu!'s own limit is lower for most users).</summary>
    public const long MaxOszBytes = 100L * 1024 * 1024;

    private const long MaxUnpackedBytes = 300L * 1024 * 1024;
    private const int MaxDifficulties = 100;
    private const int MaxEntries = 2000;
    private const long MaxPreviewBytes = 20L * 1024 * 1024;

    // One upload at a time: new ids are "one past the highest", so two at once would collide.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IBeatmapRepository _beatmaps;
    private readonly IUserRepository _users;
    private readonly CustomBeatmapStore _store;
    private readonly OsuFileCache _files;
    private readonly TimeProvider _time;
    private readonly ILogger<CustomBeatmapImporter> _logger;
    private readonly IBeatmapDifficulty? _difficulty;
    private readonly IStaffLogRepository? _log;
    private readonly OsuWebCustomSets? _website;
    private readonly OsuWebInterop? _interop;
    private readonly BeatmapService? _resolver;

    public CustomBeatmapImporter(
        IBeatmapRepository beatmaps,
        IUserRepository users,
        CustomBeatmapStore store,
        OsuFileCache files,
        TimeProvider time,
        ILogger<CustomBeatmapImporter> logger,
        IPerformanceCalculator? performance = null,
        IStaffLogRepository? log = null,
        OsuWebCustomSets? website = null,
        OsuWebInterop? interop = null,
        BeatmapService? resolver = null)
    {
        _beatmaps = beatmaps;
        _users = users;
        _store = store;
        _files = files;
        _time = time;
        _logger = logger;
        _difficulty = performance as IBeatmapDifficulty;
        _log = log;
        _website = website;
        _interop = interop;
        _resolver = resolver;
    }

    /// <summary>
    /// Stores <paramref name="osz"/> as a new custom set, or as the new version of custom set
    /// <paramref name="updateSetId"/>, uploaded by <paramref name="uploaderId"/>. Throws
    /// <see cref="CustomUploadException"/> when the file can't be taken.
    /// </summary>
    public async Task<CustomUploadResult> ImportAsync(byte[] osz, int uploaderId, int? updateSetId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(osz);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await ImportCoreAsync(osz, uploaderId, updateSetId, cancellationToken).ConfigureAwait(false);
            await LogAsync(result.SetId, uploaderId, result.Updated ? "updated" : "uploaded",
                Invariant($"{result.Artist} - {result.Title} ({result.Creator}), {result.Difficulties.Count} difficulties"), cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (CustomUploadException ex)
        {
            await LogAsync(updateSetId ?? 0, uploaderId, "rejected", ex.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<CustomUploadResult> ImportCoreAsync(byte[] osz, int uploaderId, int? updateSetId, CancellationToken cancellationToken)
    {
        if (osz.Length > MaxOszBytes)
            throw new CustomUploadException(Invariant($"The file is too big (the limit is {MaxOszBytes / 1024 / 1024} MB)."));

        var package = ReadPackage(osz);
        var first = package.Maps[0].File;
        var artist = first.Get("Metadata", "Artist") ?? string.Empty;
        var title = first.Get("Metadata", "Title") ?? string.Empty;
        var creator = first.Get("Metadata", "Creator") ?? string.Empty;
        if (artist.Length == 0 || title.Length == 0)
            throw new CustomUploadException("The difficulties need an artist and a title.");
        if (package.Maps.Any(m => !string.Equals(m.File.Get("Metadata", "Artist"), artist, StringComparison.Ordinal)
                               || !string.Equals(m.File.Get("Metadata", "Title"), title, StringComparison.Ordinal)))
            throw new CustomUploadException("Every difficulty needs the same artist and title.");
        var duplicate = package.Maps.GroupBy(m => m.Version, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new CustomUploadException(Invariant($"Two difficulties are called \"{duplicate.Key}\"."));

        // An update keeps the set's id, its owner, and the ids of difficulties it still has.
        IReadOnlyList<Beatmap> existing = [];
        int setId;
        int ownerId;
        if (updateSetId is { } updating)
        {
            if (updating < Beatmap.CustomIdFloor)
                throw new CustomUploadException("Only custom sets can be updated.");
            existing = await _beatmaps.GetBySetAsync(updating, cancellationToken).ConfigureAwait(false);
            if (existing.Count == 0)
                throw new CustomUploadException(Invariant($"Set {updating} doesn't exist."));
            if (existing.Any(b => b.Status is RankedStatus.Ranked or RankedStatus.Approved or RankedStatus.Qualified or RankedStatus.Loved))
                throw new CustomUploadException("The set is ranked, qualified or loved; set it back to pending before updating it.");
            setId = updating;
            ownerId = (_website is null ? null : await _website.OwnerAsync(setId, cancellationToken).ConfigureAwait(false)) is { } owner and > 0
                ? owner
                : await OwnerAsync(creator, uploaderId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            setId = await _beatmaps.ReserveCustomSetIdAsync(cancellationToken).ConfigureAwait(false);
            ownerId = await OwnerAsync(creator, uploaderId, cancellationToken).ConfigureAwait(false);
        }

        // A difficulty already on the server (an official map, another custom set) can't be uploaded again.
        foreach (var map in package.Maps)
        {
            if (await _beatmaps.GetByMd5Async(map.OriginalMd5, cancellationToken).ConfigureAwait(false) is { } known && known.SetId != setId)
                throw new CustomUploadException(Invariant($"\"{map.Version}\" is already on the server (beatmap {known.Id})."));
        }

        var nextId = await _beatmaps.ReserveCustomBeatmapIdAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var maps = new List<(PackagedMap Map, Beatmap Beatmap, byte[] Bytes)>();
        foreach (var map in package.Maps)
        {
            var id = existing.FirstOrDefault(b => string.Equals(b.Version, map.Version, StringComparison.OrdinalIgnoreCase))?.Id ?? nextId++;
            if (id > OsuWebBeatmaps.MaxId)
                throw new CustomUploadException("The server is out of custom beatmap ids.");
            map.File.Set("Metadata", "BeatmapID", id.ToString(CultureInfo.InvariantCulture));
            map.File.Set("Metadata", "BeatmapSetID", setId.ToString(CultureInfo.InvariantCulture));
            var bytes = map.File.ToBytes();
            maps.Add((map, ToBeatmap(map.File, id, setId, OsuFileText.Md5(bytes), package.HasStoryboardFile, now), bytes));
        }

        var rebuilt = Rebuild(osz, maps.ToDictionary(m => m.Map.EntryName, m => m.Bytes, StringComparer.Ordinal));
        await _store.SaveAsync(setId, rebuilt, package.Background, package.Preview, cancellationToken).ConfigureAwait(false);

        var difficulties = new List<CustomUploadDifficulty>();
        foreach (var (_, beatmap, bytes) in maps)
        {
            await _files.StoreAsync(beatmap.Id, bytes, cancellationToken).ConfigureAwait(false);
            if (_difficulty is not null)
            {
                try
                {
                    if (await _difficulty.DifficultyAsync(beatmap.Id, beatmap.Mode, cancellationToken).ConfigureAwait(false) is { } difficulty)
                    {
                        beatmap.StarRating = Math.Round(difficulty.StarRating, 2);
                        beatmap.MaxCombo = difficulty.MaxCombo;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Stored either way; the star rating stays 0 until the map is recalculated.
                    _logger.LogWarning(ex, "Couldn't compute the difficulty of custom beatmap {BeatmapId}.", beatmap.Id);
                }
            }

            await _beatmaps.UpsertAsync(beatmap, cancellationToken).ConfigureAwait(false);
            difficulties.Add(new CustomUploadDifficulty(beatmap.Id, beatmap.Version, beatmap.Mode, beatmap.StarRating, beatmap.Md5));
        }

        _resolver?.Remember(maps.Select(m => m.Beatmap));
        await FinishOnWebsiteAsync(setId, ownerId, difficulties.Select(d => d.BeatmapId).ToList(), rebuilt.Length, first, cancellationToken).ConfigureAwait(false);
        return new CustomUploadResult(setId, artist, title, creator, ownerId, existing.Count > 0, difficulties);
    }

    private async Task FinishOnWebsiteAsync(int setId, int ownerId, IReadOnlyCollection<int> beatmapIds, long size, OsuFileText first, CancellationToken cancellationToken)
    {
        if (_website is not null)
        {
            await _website.FinishUploadAsync(setId, ownerId, beatmapIds, _store.HasBackground(setId), size,
                first.Get("Metadata", "ArtistUnicode"), first.Get("Metadata", "TitleUnicode"), cancellationToken).ConfigureAwait(false);
        }

        if (_interop is { IsConfigured: true })
        {
            try
            {
                await _interop.IndexBeatmapsetAsync(setId, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // The set is stored either way; only the listing lags until the next re-index.
                _logger.LogWarning(ex, "Uploaded beatmap set {SetId} but osu-web search indexing failed.", setId);
            }
        }
    }

    /// <summary>The mapper's account when one has the file's creator name, else the uploader.</summary>
    private async Task<int> OwnerAsync(string creator, int uploaderId, CancellationToken cancellationToken) =>
        creator.Length > 0 && await _users.GetByUsernameAsync(creator, cancellationToken).ConfigureAwait(false) is { } mapper
            ? mapper.Id
            : uploaderId;

    private static Beatmap ToBeatmap(OsuFileText file, int id, int setId, string md5, bool storyboardFile, DateTimeOffset now)
    {
        var objects = file.HitObjects;
        var events = file.EventKinds;
        var overall = (float?)file.GetDouble("Difficulty", "OverallDifficulty");
        return new Beatmap
        {
            Id = id,
            SetId = setId,
            Md5 = md5,
            Artist = file.Get("Metadata", "Artist") ?? string.Empty,
            Title = file.Get("Metadata", "Title") ?? string.Empty,
            Version = file.Get("Metadata", "Version") ?? string.Empty,
            Creator = file.Get("Metadata", "Creator") ?? string.Empty,
            Mode = Math.Clamp(file.GetInt("General", "Mode"), 0, 3),
            Status = RankedStatus.Pending,
            OsuApiStatus = 0,
            TotalLength = objects.LastMs / 1000,
            LastUpdate = now,
            Details = new BeatmapDetails
            {
                CircleSize = (float?)file.GetDouble("Difficulty", "CircleSize"),
                OverallDifficulty = overall,
                // Old files have no approach rate: it was the overall difficulty.
                ApproachRate = (float?)file.GetDouble("Difficulty", "ApproachRate") ?? overall,
                DrainRate = (float?)file.GetDouble("Difficulty", "HPDrainRate"),
                Bpm = file.Bpm is { } bpm ? (float)bpm : null,
                HitLength = Math.Max(0, objects.LastMs - objects.FirstMs - file.BreakMs) / 1000,
                CountCircles = objects.Circles,
                CountSliders = objects.Sliders,
                CountSpinners = objects.Spinners,
                Source = file.Get("Metadata", "Source") ?? string.Empty,
                Tags = file.Get("Metadata", "Tags") ?? string.Empty,
                Video = events.Video,
                Storyboard = events.Storyboard || storyboardFile,
            },
        };
    }

    private sealed record PackagedMap(string EntryName, OsuFileText File, string OriginalMd5)
    {
        public string Version => File.Get("Metadata", "Version") ?? string.Empty;
    }

    private sealed record Package(List<PackagedMap> Maps, (string Extension, byte[] Bytes)? Background, byte[]? Preview, bool HasStoryboardFile);

    /// <summary>Reads the difficulties, background and audio out of the archive (bounded, so a zip bomb can't hurt).</summary>
    private static Package ReadPackage(byte[] osz)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(osz, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new CustomUploadException("That isn't an .osz file.");
        }

        using (archive)
        {
            if (archive.Entries.Count > MaxEntries)
                throw new CustomUploadException("The file has too many entries.");
            if (archive.Entries.Sum(e => e.Length) > MaxUnpackedBytes)
                throw new CustomUploadException("The file unpacks to too much data.");

            var maps = new List<PackagedMap>();
            foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)))
            {
                var bytes = ReadEntry(entry);
                var file = OsuFileText.Parse(bytes);
                if (!file.IsBeatmap)
                    throw new CustomUploadException(Invariant($"{entry.Name} isn't an osu! beatmap."));
                maps.Add(new PackagedMap(entry.FullName, file, OsuFileText.Md5(bytes)));
            }
            if (maps.Count == 0)
                throw new CustomUploadException("The file has no difficulties (.osu files).");
            if (maps.Count > MaxDifficulties)
                throw new CustomUploadException(Invariant($"The set has more than {MaxDifficulties} difficulties."));

            (string, byte[])? background = null;
            foreach (var name in maps.Select(m => m.File.Background).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var extension = Path.GetExtension(name).ToLowerInvariant();
                if (extension is ".jpg" or ".jpeg" or ".png" && Find(archive, name) is { } image)
                {
                    background = (extension, ReadEntry(image));
                    break;
                }
            }

            byte[]? preview = null;
            var audio = maps[0].File.Get("General", "AudioFilename");
            if (audio is not null && audio.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                && Find(archive, audio) is { Length: > 0 and <= MaxPreviewBytes } song)
                preview = ReadEntry(song);

            var storyboardFile = archive.Entries.Any(e => e.FullName.EndsWith(".osb", StringComparison.OrdinalIgnoreCase));
            return new Package(maps, background, preview, storyboardFile);
        }
    }

    private static ZipArchiveEntry? Find(ZipArchive archive, string path)
    {
        var wanted = path.Replace('\\', '/').TrimStart('/');
        return archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var copy = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        catch (InvalidDataException)
        {
            throw new CustomUploadException(Invariant($"{entry.FullName} is damaged."));
        }
    }

    /// <summary>The archive again, with the difficulties replaced by their rewritten files.</summary>
    private static byte[] Rebuild(byte[] osz, Dictionary<string, byte[]> replaced)
    {
        using var source = new ZipArchive(new MemoryStream(osz, writable: false), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                var copy = target.CreateEntry(entry.FullName, replaced.ContainsKey(entry.FullName) ? CompressionLevel.Optimal : CompressionLevel.Fastest);
                copy.LastWriteTime = entry.LastWriteTime;
                using var to = copy.Open();
                if (replaced.TryGetValue(entry.FullName, out var bytes))
                {
                    to.Write(bytes);
                }
                else
                {
                    using var from = entry.Open();
                    from.CopyTo(to);
                }
            }
        }
        return output.ToArray();
    }

    private async Task LogAsync(int setId, int actorId, string result, string summary, CancellationToken cancellationToken)
    {
        if (_log is null)
            return;
        try
        {
            await _log.AddAsync(new StaffLogEntry
            {
                CreatedAt = _time.GetUtcNow().UtcDateTime,
                ActorId = actorId,
                Action = LogAction,
                TargetId = setId == 0 ? null : setId,
                Summary = summary.Length > 512 ? summary[..512] : summary,
                Result = result,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Couldn't write the staff log for beatmap set upload {SetId}.", setId);
        }
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
