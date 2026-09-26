using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;

using Microsoft.Extensions.Logging;

namespace TomYum.Performance;

/// <summary>
/// On-disk cache of raw single-difficulty <c>.osu</c> files, keyed by beatmap id.
/// </summary>
/// <remarks>
/// The in-process difficulty cache only holds computed <c>DifficultyAttributes</c>, so without this
/// every distinct (beatmap, ruleset, mods) triple re-downloaded the same <c>.osu</c> from
/// osu.ppy.sh. This caches the source bytes on disk: it survives restarts, is shared across every
/// mods/ruleset combination of the same beatmap, and is the seam that will serve locally-hosted
/// custom beatmaps (id ≥ <c>100000000</c>, ADR-0009) once upload lands — a custom map's <c>.osu</c>
/// dropped into the cache directory is served without ever hitting osu.ppy.sh. See
/// <c>docs/ROADMAP.md</c> Phase 0.
/// </remarks>
public sealed class OsuFileCache
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client used for <c>.osu</c> downloads.</summary>
    public const string HttpClientName = "osu-files";

    private static readonly CompositeFormat DownloadUrl =
        CompositeFormat.Parse("https://osu.ppy.sh/osu/{0}");

    private readonly string _directory;
    private readonly HttpClient _http;
    private readonly ILogger<OsuFileCache> _log;

    /// <summary>Creates the cache over <paramref name="directory"/>, creating it if absent.</summary>
    public OsuFileCache(string directory, HttpClient http, ILogger<OsuFileCache> log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(log);

        _directory = directory;
        _http = http;
        _log = log;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Directory holding the cached <c>{beatmapId}.osu</c> files.</summary>
    public string CacheDirectory => _directory;

    /// <summary>
    /// Returns the raw <c>.osu</c> bytes for <paramref name="beatmapId"/>, serving from the disk
    /// cache when present and otherwise downloading from osu.ppy.sh and caching the result.
    /// </summary>
    /// <remarks>
    /// Throws when the download fails (e.g. a custom id not on osu.ppy.sh with no local file); the
    /// caller treats that as "no attributes" → 0 pp, matching the previous direct-download behaviour.
    /// </remarks>
    public async Task<byte[]> GetAsync(int beatmapId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_directory, beatmapId.ToString(CultureInfo.InvariantCulture) + ".osu");

        if (File.Exists(path))
        {
            try
            {
                var cached = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (cached.Length > 0)
                    return cached;
            }
            catch (IOException ex)
            {
                // A truncated/locked cached file: fall through and re-download rather than fail.
                _log.LogDebug(ex, "osu-cache: reread of {Path} failed; re-downloading", path);
            }
        }

        var url = string.Format(CultureInfo.InvariantCulture, DownloadUrl, beatmapId);
        var bytes = await _http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
            throw new InvalidOperationException($"osu.ppy.sh returned an empty .osu for beatmap {beatmapId}.");

        await WriteAtomicAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        _log.LogDebug("osu-cache: downloaded + cached beatmap {BeatmapId} ({Bytes} bytes)", beatmapId, bytes.Length);
        return bytes;
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        // Write to a unique temp file then move into place, so a concurrent reader never observes a
        // partially-written .osu. Move is atomic on the same volume; overwrite tolerates the race
        // where two callers fetch the same id at once.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; a stray .tmp is harmless and reaped on the next write.
                }
            }
        }
    }
}
