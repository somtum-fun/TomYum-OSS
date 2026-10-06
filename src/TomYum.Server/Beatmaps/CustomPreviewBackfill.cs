using System.IO.Compression;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// In the background at start: custom sets stored before previews were cut (their preview is the whole
/// song, or none for an Ogg song) get one made from their song (<see cref="PreviewClips"/>). Sets whose
/// preview is already a clip are left alone; without ffmpeg it stops at the first set.
/// </summary>
public sealed class CustomPreviewBackfill : BackgroundService
{
    private readonly CustomBeatmapStore _store;
    private readonly ILogger<CustomPreviewBackfill> _logger;

    public CustomPreviewBackfill(CustomBeatmapStore store, ILogger<CustomPreviewBackfill> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>Makes the missing previews; returns how many were made.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var made = 0;
        foreach (var setId in _store.SetIds().ToList())
        {
            if (_store.PreviewPath(setId) is { } existing && new FileInfo(existing).Length <= PreviewClips.MaxClipBytes)
                continue;

            if (ReadSong(_store.OszPath(setId)) is not { } song)
                continue;

            if (!await _store.SavePreviewAsync(setId, song, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("Custom previews: couldn't make set {SetId}'s (is ffmpeg installed?); stopping.", setId);
                break;
            }
            made++;
        }

        if (made > 0)
            _logger.LogInformation("Custom previews: made {Count} from their songs.", made);
        return made;
    }

    // The song and preview point of the set's first difficulty that names one.
    internal static PreviewSong? ReadSong(string oszPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(oszPath);
            foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)))
            {
                byte[] bytes;
                using (var stream = entry.Open())
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    bytes = memory.ToArray();
                }

                var file = OsuFileText.Parse(bytes);
                var audio = file.Get("General", "AudioFilename");
                if (string.IsNullOrEmpty(audio))
                    continue;

                var songEntry = archive.Entries.FirstOrDefault(e => e.FullName.Equals(audio, StringComparison.OrdinalIgnoreCase));
                if (songEntry is null || songEntry.Length is 0 or > 20L * 1024 * 1024)
                    return null;

                using var song = songEntry.Open();
                using var songBytes = new MemoryStream();
                song.CopyTo(songBytes);
                return new PreviewSong(songBytes.ToArray(), Path.GetExtension(audio).ToLowerInvariant(), file.GetInt("General", "PreviewTime", -1));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
        }
        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Custom previews: the backfill failed.");
        }
    }
}
