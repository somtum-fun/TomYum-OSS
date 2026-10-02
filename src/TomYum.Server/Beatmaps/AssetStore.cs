using System.Text.RegularExpressions;
using SkiaSharp;

namespace TomYum.Server.Beatmaps;

/// <summary>One image of a pair: its size on disk and in pixels.</summary>
public sealed record AssetImage(long Bytes, int Width, int Height);

/// <summary>
/// An image as the assets host serves it, at normal size and double (<c>@2x</c>), like osu!'s:
/// <c>profile-badges/ndc-2023.png</c> and <c>profile-badges/ndc-2023@2x.png</c>.
/// </summary>
/// <param name="Path">The normal one's path (<c>profile-badges/ndc-2023.png</c>).</param>
/// <param name="Name">Its file name (<c>ndc-2023.png</c>).</param>
/// <param name="Normal">The normal one; null when only the double is there.</param>
/// <param name="Retina">The <c>@2x</c> one; null when only the normal is there.</param>
public sealed record AssetPair(string Path, string Name, AssetImage? Normal, AssetImage? Retina);

/// <summary>A folder of the assets host: the folders in it and its image pairs.</summary>
public sealed record AssetFolder(string Path, IReadOnlyList<string> Folders, IReadOnlyList<AssetPair> Images);

/// <summary>
/// The images the assets host (<c>assets.&lt;domain&gt;</c>) serves, at the same paths as osu!'s
/// (<c>assets.ppy.sh</c>), managed from the website's admin panel: profile badges, tournament
/// banners (any folders, like osu!'s <c>tournament-banners/official/owc2025/profile/</c>) and medal
/// icons. Every image is a pair, normal and <c>@2x</c>; the names are the ones staff give, never made up.
/// Kept in the asset cache (<c>Assets:CachePath</c>), next to what's fetched from osu! on first use.
/// </summary>
public sealed partial class AssetStore
{
    /// <summary>The folders staff manage (and the assets host serves).</summary>
    public static readonly IReadOnlyList<string> Roots = [AssetsEndpoints.BadgeFolder, BannerFolder, "medals/client"];

    public const string BannerFolder = "tournament-banners";

    /// <summary>Biggest image taken.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    private readonly string _root;
    private readonly IHttpClientFactory _http;

    public AssetStore(IConfiguration configuration, IHttpClientFactory http)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _root = AssetsEndpoints.CacheDirectory(configuration, string.Empty);
        _http = http;
    }

    /// <summary>Where the images are on disk (the asset cache's root).</summary>
    public string RootDirectory => _root;

    /// <summary>
    /// Whether <paramref name="path"/> is a folder or file staff may manage: under one of the
    /// <see cref="Roots"/>, every part plain (letters, digits, <c>. _ -</c>, <c>@</c>; no <c>..</c>).
    /// </summary>
    public static bool IsManagedPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 300 || path.EndsWith('/'))
            return false;
        if (!Roots.Any(root => path == root || path.StartsWith(root + "/", StringComparison.Ordinal)))
            return false;
        return path.Split('/').All(part => SafePart().IsMatch(part));
    }

    /// <summary>Whether <paramref name="path"/> is an image file staff may manage (and of what kind).</summary>
    public static bool IsImagePath(string? path) =>
        IsManagedPath(path) && Extensions.Contains(Path.GetExtension(path!).ToLowerInvariant()) && !Roots.Contains(path);

    /// <summary>The <c>@2x</c> one's path: <c>a/ndc-2023.png</c> → <c>a/ndc-2023@2x.png</c>.</summary>
    public static string RetinaPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var extension = Path.GetExtension(path);
        return path[..^extension.Length] + "@2x" + extension;
    }

    /// <summary>The folder's subfolders and image pairs (A to Z); null when it isn't one staff manage.</summary>
    public AssetFolder? List(string folder)
    {
        if (!IsManagedPath(folder))
            return null;
        var directory = FullPath(folder);
        if (!Directory.Exists(directory))
            return new AssetFolder(folder, [], []);

        var folders = Directory.GetDirectories(directory)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => SafePart().IsMatch(name))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var files = Directory.GetFiles(directory)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => Extensions.Contains(Path.GetExtension(name).ToLowerInvariant()) && SafePart().IsMatch(name))
            .ToHashSet(StringComparer.Ordinal);
        var images = new List<AssetPair>();
        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            var extension = Path.GetExtension(file);
            var stem = file[..^extension.Length];
            if (stem.EndsWith("@2x", StringComparison.Ordinal))
            {
                // A double without its normal one still shows (as the pair it belongs to).
                var normalName = stem[..^3] + extension;
                if (files.Contains(normalName))
                    continue;
                images.Add(Pair($"{folder}/{normalName}"));
                continue;
            }
            images.Add(Pair($"{folder}/{file}"));
        }
        return new AssetFolder(folder, folders, images);
    }

    /// <summary>One pair (either may be missing).</summary>
    public AssetPair Pair(string path) =>
        new(path, Path.GetFileName(path), Info(FullPath(path)), Info(FullPath(RetinaPath(path))));

    /// <summary>
    /// Saves a pair at <paramref name="path"/> (the normal one's): both given, or only the double
    /// (the normal is made from it, at half the size), or only the normal. Tournament banners are
    /// JPEG, like osu!'s (the website asks for <c>….jpg</c>). The problem, if any.
    /// </summary>
    public async Task<string?> SaveAsync(string path, byte[]? normal, byte[]? retina, CancellationToken cancellationToken = default)
    {
        if (!IsImagePath(path) || path.Contains("@2x", StringComparison.Ordinal))
            return "Give the image a plain name in one of the folders (letters, digits, - and _, then .png or .jpg).";
        if (path.StartsWith(BannerFolder + "/", StringComparison.Ordinal) && !path.EndsWith(".jpg", StringComparison.Ordinal))
            return "Tournament banners are .jpg (the website asks for them by that name).";
        if (normal is null && retina is null)
            return "Choose the image (normal size, the @2x one, or both).";
        if ((normal?.Length ?? 0) > MaxBytes || (retina?.Length ?? 0) > MaxBytes)
            return "That image is over 10 MB.";
        if ((normal is not null && !IsImage(normal)) || (retina is not null && !IsImage(retina)))
            return "That isn't an image (PNG, JPEG, GIF or WebP).";

        if (normal is null && retina is not null)
        {
            normal = Half(retina, Path.GetExtension(path));
            if (normal is null)
                return "The normal size couldn't be made from the @2x one; upload it too.";
        }

        if (normal is not null)
            await WriteAsync(path, normal, cancellationToken).ConfigureAwait(false);
        if (retina is not null)
            await WriteAsync(RetinaPath(path), retina, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>Removes a pair (both sizes); false when neither was there.</summary>
    public bool Delete(string path)
    {
        if (!IsImagePath(path))
            return false;
        var removed = false;
        foreach (var file in new[] { FullPath(path), FullPath(RetinaPath(path)) })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
                removed = true;
            }
        }
        return removed;
    }

    /// <summary>Renames a pair in its folder (the extension stays); the problem, if any.</summary>
    public string? Rename(string path, string newName)
    {
        ArgumentNullException.ThrowIfNull(newName);
        if (!IsImagePath(path))
            return "That isn't one of the images.";
        var extension = Path.GetExtension(path);
        newName = newName.Trim();
        if (newName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            newName = newName[..^extension.Length];
        var target = $"{Path.GetDirectoryName(path)!.Replace('\\', '/')}/{newName}{extension}";
        if (newName.Length == 0 || newName.Contains("@2x", StringComparison.Ordinal) || !IsImagePath(target))
            return "Give a plain name (letters, digits, - and _).";
        if (target == path)
            return null;
        if (File.Exists(FullPath(target)) || File.Exists(FullPath(RetinaPath(target))))
            return $"There's already one called {newName}{extension}.";
        if (!File.Exists(FullPath(path)) && !File.Exists(FullPath(RetinaPath(path))))
            return "That image isn't there.";

        foreach (var (from, to) in new[] { (path, target), (RetinaPath(path), RetinaPath(target)) })
        {
            if (File.Exists(FullPath(from)))
                File.Move(FullPath(from), FullPath(to));
        }
        return null;
    }

    /// <summary>
    /// Copies an image from osu!'s assets host to the same path here, both sizes (whichever osu! has):
    /// <c>https://assets.ppy.sh/profile-badges/ndc-2023.png</c> or just <c>profile-badges/ndc-2023.png</c>.
    /// [the path, null] or [null, the problem].
    /// </summary>
    public async Task<(string? Path, string? Error)> ImportAsync(string link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        var path = link.Trim();
        const string osu = "https://assets.ppy.sh/";
        if (path.StartsWith(osu, StringComparison.OrdinalIgnoreCase))
            path = path[osu.Length..];
        path = Uri.UnescapeDataString(path.Split('?')[0]);
        // The @2x one's link is fine too: its pair is the same.
        if (Path.GetFileNameWithoutExtension(path).EndsWith("@2x", StringComparison.Ordinal))
            path = path.Replace("@2x.", ".", StringComparison.Ordinal);
        if (!IsImagePath(path))
            return (null, $"{link.Trim()}: not an osu! asset link in {string.Join(", ", Roots)}.");

        using var http = _http.CreateClient(AssetsEndpoints.HttpClientName);
        async Task<byte[]?> FetchAsync(string relative)
        {
            using var response = await http.GetAsync(osu + relative, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length <= MaxBytes && IsImage(bytes) ? bytes : null;
        }

        try
        {
            var normal = await FetchAsync(path).ConfigureAwait(false);
            var retina = await FetchAsync(RetinaPath(path)).ConfigureAwait(false);
            if (normal is null && retina is null)
                return (null, $"{link.Trim()}: osu! doesn't have it.");
            var problem = await SaveAsync(path, normal, retina, cancellationToken).ConfigureAwait(false);
            return problem is null ? (path, null) : (null, $"{link.Trim()}: {problem}");
        }
        catch (HttpRequestException ex)
        {
            return (null, $"{link.Trim()}: couldn't reach osu! ({ex.Message}).");
        }
    }

    /// <summary>Whether <paramref name="bytes"/> decode as an image.</summary>
    public static bool IsImage(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var codec = SKCodec.Create(new SKMemoryStream(bytes));
        return codec is not null && codec.Info.Width > 0 && codec.Info.Height > 0;
    }

    /// <summary>The image at half its size, in the file's own format (a GIF is kept as it is: halving it would stop it moving).</summary>
    public static byte[]? Half(byte[] source, string extension)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(extension);
        if (extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
            return source;
        using var original = SKBitmap.Decode(source);
        if (original is null || original.Width < 2 || original.Height < 2)
            return null;

        var info = new SKImageInfo((original.Width + 1) / 2, (original.Height + 1) / 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(original);
        surface.Canvas.DrawImage(image, new SKRect(0, 0, info.Width, info.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var snapshot = surface.Snapshot();
        var (format, quality) = extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => (SKEncodedImageFormat.Jpeg, 92),
            ".webp" => (SKEncodedImageFormat.Webp, 92),
            _ => (SKEncodedImageFormat.Png, 100),
        };
        using var encoded = snapshot.Encode(format, quality);
        return encoded?.ToArray();
    }

    private string FullPath(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        // IsManagedPath keeps paths plain; this is the last word on staying inside the cache.
        if (!full.StartsWith(_root, StringComparison.Ordinal))
            throw new InvalidOperationException("Path outside the asset cache.");
        return full;
    }

    private static AssetImage? Info(string file)
    {
        if (!File.Exists(file))
            return null;
        using var codec = SKCodec.Create(file);
        return new AssetImage(new FileInfo(file).Length, codec?.Info.Width ?? 0, codec?.Info.Height ?? 0);
    }

    private async Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        var full = FullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, data, cancellationToken).ConfigureAwait(false);
        File.Move(temp, full, overwrite: true);
    }

    // One part of a path: starts with a letter or digit, then letters, digits, . _ - @ (no "..").
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._@-]{0,120}$")]
    private static partial Regex SafePart();
}
