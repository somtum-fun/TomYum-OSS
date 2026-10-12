using System.Collections.Concurrent;
using System.Globalization;
using SkiaSharp;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// Files of custom beatmap sets (ids from <see cref="TomYum.Domain.Entities.Beatmap.CustomIdFloor"/>),
/// which exist only on this server: the <c>.osz</c> players download, the background and the
/// preview audio (30 seconds from the song's preview point, <see cref="PreviewClips"/>). Covers and thumbnails are made from the background on first use and kept. Lives
/// in <c>Beatmaps:Path</c> (default <c>data/beatmaps</c>).
/// </summary>
public sealed class CustomBeatmapStore
{
    public const string PathKey = "Beatmaps:Path";
    public const string DefaultPath = "data/beatmaps";

    /// <summary>The website's cover sizes (osu-web's <c>BeatmapsetCover</c>), and stable's thumbnails.</summary>
    public static readonly IReadOnlyDictionary<string, (int Width, int Height)> CoverSizes = new Dictionary<string, (int, int)>(StringComparer.Ordinal)
    {
        ["cover"] = (900, 250), ["cover@2x"] = (1800, 500),
        ["card"] = (400, 140), ["card@2x"] = (800, 280),
        ["list"] = (150, 150), ["list@2x"] = (300, 300),
        ["slimcover"] = (1920, 360), ["slimcover@2x"] = (3840, 720),
        ["thumb"] = (80, 60), ["thumbl"] = (160, 120),
    };

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png"];

    // Covers being made at once (each decode of a background can take a few hundred MB).
    private static readonly SemaphoreSlim CoverGate = new(2, 2);

    private readonly string _root;

    // Sets whose background couldn't be made into covers, by that file's write time: not tried again until it changes.
    private readonly ConcurrentDictionary<int, DateTime> _unreadable = new();

    /// <summary>The ffmpeg previews are made with.</summary>
    public string Ffmpeg { get; }

    public CustomBeatmapStore(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _root = Path.GetFullPath(configuration[PathKey] ?? DefaultPath);
        Ffmpeg = configuration[PreviewClips.FfmpegKey] ?? "ffmpeg";
    }

    /// <summary>The custom sets stored here.</summary>
    public IEnumerable<int> SetIds()
    {
        var directory = Path.Combine(_root, "osz");
        if (!Directory.Exists(directory))
            yield break;
        foreach (var file in Directory.EnumerateFiles(directory, "*.osz"))
        {
            if (int.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                yield return id;
        }
    }

    public string OszPath(int setId) => Path.Combine(_root, "osz", Invariant(setId) + ".osz");

    public bool HasSet(int setId) => File.Exists(OszPath(setId));

    /// <summary>Stores a set's files, replacing earlier ones (covers and thumbnails are made again).</summary>
    public async Task SaveAsync(int setId, byte[] osz, (string Extension, byte[] Bytes)? background, PreviewSong? song, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(osz);
        await WriteAsync(OszPath(setId), osz, cancellationToken).ConfigureAwait(false);

        foreach (var extension in ImageExtensions)
            DeleteIfExists(Path.Combine(_root, "bg", Invariant(setId) + extension));
        if (background is { } bg && ImageExtensions.Contains(bg.Extension))
            await WriteAsync(Path.Combine(_root, "bg", Invariant(setId) + bg.Extension), bg.Bytes, cancellationToken).ConfigureAwait(false);

        await SavePreviewAsync(setId, song, cancellationToken).ConfigureAwait(false);

        var covers = Path.Combine(_root, "covers", Invariant(setId));
        if (Directory.Exists(covers))
            Directory.Delete(covers, recursive: true);
        _unreadable.TryRemove(setId, out _);
    }

    /// <summary>
    /// Makes and stores a set's preview from its song (<see cref="PreviewClips"/>); without ffmpeg an MP3
    /// song is kept whole, as before. Whether a clip was made.
    /// </summary>
    public async Task<bool> SavePreviewAsync(int setId, PreviewSong? song, CancellationToken cancellationToken = default)
    {
        var preview = Path.Combine(_root, "preview", Invariant(setId) + ".mp3");
        DeleteIfExists(preview);
        if (song is null)
            return false;

        var clip = await PreviewClips.MakeAsync(song.Bytes, song.Extension, song.PreviewTime, Ffmpeg, cancellationToken).ConfigureAwait(false);
        if (clip is not null)
        {
            await WriteAsync(preview, clip, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (song.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            await WriteAsync(preview, song.Bytes, cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>The preview audio, or null.</summary>
    public string? PreviewPath(int setId)
    {
        var path = Path.Combine(_root, "preview", Invariant(setId) + ".mp3");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// A cover or thumbnail (<see cref="CoverSizes"/>) as a JPEG file, made from the background on
    /// first use; null when the set has no (readable) background or the size is unknown. A background
    /// that can't be read, or is bigger than <see cref="ImageLimits.MaxSide"/> on a side, is never
    /// decoded, and is remembered as such until the set's files change.
    /// </summary>
    public string? CoverPath(int setId, string size)
    {
        if (!CoverSizes.TryGetValue(size, out var dimensions))
            return null;
        var path = Path.Combine(_root, "covers", Invariant(setId), size + ".jpg");
        if (File.Exists(path))
            return path;

        var background = BackgroundPath(setId);
        if (background is null)
            return null;
        var written = File.GetLastWriteTimeUtc(background);
        if (_unreadable.TryGetValue(setId, out var failed) && failed == written)
            return null;

        CoverGate.Wait();
        try
        {
            if (File.Exists(path))
                return path;

            using var codec = SKCodec.Create(background);
            if (ImageLimits.Size(codec) is not { } original)
            {
                _unreadable[setId] = written;
                return null;
            }
            // Decoded only as big as the cover needs, where the format allows it.
            using var source = ImageLimits.Decode(codec, Math.Max((double)dimensions.Width / original.Width, (double)dimensions.Height / original.Height));
            if (source is null)
            {
                _unreadable[setId] = written;
                return null;
            }

            var jpeg = Crop(source, dimensions.Width, dimensions.Height);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, jpeg);
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        finally
        {
            CoverGate.Release();
        }
    }

    /// <summary>Whether the set has a background (so the website can show covers).</summary>
    public bool HasBackground(int setId) => BackgroundPath(setId) is not null;

    /// <summary>The set's background as uploaded, or null.</summary>
    public string? BackgroundPath(int setId) =>
        ImageExtensions.Select(e => Path.Combine(_root, "bg", Invariant(setId) + e)).FirstOrDefault(File.Exists);

    /// <summary>The image scaled to cover <paramref name="width"/>×<paramref name="height"/>, centred and cropped, as JPEG.</summary>
    public static byte[] Crop(SKBitmap source, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        var scale = Math.Max((double)width / source.Width, (double)height / source.Height);
        var scaledWidth = (int)Math.Ceiling(source.Width * scale);
        var scaledHeight = (int)Math.Ceiling(source.Height * scale);

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        using var image = SKImage.FromBitmap(source);
        var destination = SKRect.Create((width - scaledWidth) / 2f, (height - scaledHeight) / 2f, scaledWidth, scaledHeight);
        canvas.DrawImage(image, destination, new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string Invariant(int id) => id.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A set's song for its preview: the file's bytes, its extension and the map's preview point (ms, -1 without).</summary>
public sealed record PreviewSong(byte[] Bytes, string Extension, int PreviewTime);
