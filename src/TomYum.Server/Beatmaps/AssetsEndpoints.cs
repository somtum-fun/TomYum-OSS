using System.Text.RegularExpressions;
using SkiaSharp;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// Serves static osu! assets on the <c>assets.&lt;domain&gt;</c> host. The devserver-patched client
/// rewrites <c>assets.ppy.sh</c> to <c>assets.&lt;domain&gt;</c>, so medal-unlock popups request their
/// icons from here (<c>/medals/client/{file}.png</c> and the <c>@2x</c> retina variant). Profile
/// badges (<c>/profile-badges/{file}.png</c>) work the same way: the website's badge images point here.
/// </summary>
/// <remarks>
/// Medal icons are cache-and-served: on first request we fetch the file from ppy's CDN, cache it to
/// disk (<c>Assets:CachePath</c>/<c>medals/client</c>, default <c>data/assets/medals/client</c>), and
/// serve every later request from the cache — no repeated CDN hits, and the same on-disk layout that
/// server-hosted custom medals will drop into later. Badges added with the Discord bot are written
/// into <c>profile-badges</c> there, so they're served the same way. (The beatmap-thumbnail host, by contrast, just
/// 301-redirects to ppy; medals are cached because custom ones won't exist on ppy's CDN.)
/// </remarks>
public static partial class AssetsEndpoints
{
    /// <summary>ppy's fixed asset CDN — the upstream medal icons are fetched from here.</summary>
    private const string UpstreamBaseUrl = "https://assets.ppy.sh";

    /// <summary>Named <see cref="System.Net.Http.HttpClient"/> used to fetch upstream assets.</summary>
    internal const string HttpClientName = "OsuAssets";

    /// <summary>Config key for the on-disk asset cache root.</summary>
    private const string CachePathKey = "Assets:CachePath";
    private const string DefaultCachePath = "data/assets";

    /// <summary>The folder (under the cache root, and on ppy's CDN) with profile badge images.</summary>
    public const string BadgeFolder = "profile-badges";

    /// <summary>Where the asset cache's <paramref name="folder"/> is on disk.</summary>
    public static string CacheDirectory(IConfiguration configuration, string folder)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Path.GetFullPath(Path.Combine(configuration[CachePathKey] ?? DefaultCachePath, folder));
    }

    // A medal icon filename is a single path segment: letters/digits/`.`/`_`/`@`/`-`, ending in `.png`.
    // Anchored + no slashes, so `..`/absolute paths can never escape the cache directory.
    [GeneratedRegex(@"^[A-Za-z0-9._@-]+\.png$")]
    private static partial Regex SafeMedalFileRegex();

    /// <summary>Whether <paramref name="file"/> is a safe medal icon filename (no path traversal).</summary>
    internal static bool IsSafeMedalFile(string file) => SafeMedalFileRegex().IsMatch(file);

    /// <summary>Maps the asset routes onto the given hosts (typically <c>assets.&lt;domain&gt;</c>).</summary>
    public static void MapAssets(this IEndpointRouteBuilder app, params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/medals/client/{file}", (string file, IHttpClientFactory http, IConfiguration configuration, ILoggerFactory logs, CancellationToken cancellationToken) =>
            HandleCachedAssetAsync("medals/client", file, http, configuration, logs, cancellationToken)).RequireHost(hosts);
        app.MapGet("/" + BadgeFolder + "/{file}", (string file, IHttpClientFactory http, IConfiguration configuration, ILoggerFactory logs, CancellationToken cancellationToken) =>
            HandleCachedAssetAsync(BadgeFolder, file, http, configuration, logs, cancellationToken)).RequireHost(hosts);
        // Tournament banners, in folders like osu!'s (tournament-banners/official/owc2025/profile/supporter_CA.jpg):
        // the ones staff put here (the admin panel's assets), else osu!'s, fetched once and kept.
        app.MapGet("/" + AssetStore.BannerFolder + "/{**path}", (string path, IHttpClientFactory http, IConfiguration configuration, ILoggerFactory logs, CancellationToken cancellationToken) =>
            HandleNestedAssetAsync(AssetStore.BannerFolder + "/" + path, http, configuration, logs, cancellationToken)).RequireHost(hosts);
        // lazer's main-menu banners (osu!'s assets.ppy.sh/menu-content.json), as staff set them here.
        app.MapGet("/" + MenuBanners.FileName, async (MenuBanners banners, CancellationToken cancellationToken) =>
            Results.Text(await banners.ContentAsync(cancellationToken).ConfigureAwait(false), "application/json")).RequireHost(hosts);
    }

    /// <summary>
    /// osu!'s old static host (<c>s.&lt;domain&gt;</c>): old clients (docs/old-clients.md) load medal images
    /// from <c>/images/achievements/{name}</c>, without an extension. They get today's medal icons (osu!
    /// no longer has the old images, and the skill medals never had any), at the old images' size.
    /// </summary>
    public static void MapStaticAssets(this IEndpointRouteBuilder app, params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/images/achievements/{file}", HandleLegacyMedalAsync).RequireHost(hosts);
    }

    /// <summary>The old medal images' size: old clients draw them as they are, so today's (193×210) came out twice as big.</summary>
    internal const int LegacyMedalWidth = 133, LegacyMedalHeight = 103;

    private static async Task<IResult> HandleLegacyMedalAsync(
        string file, IHttpClientFactory http, IConfiguration configuration, ILoggerFactory logs, CancellationToken cancellationToken)
    {
        file = file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? file : file + ".png";
        if (!IsSafeMedalFile(file))
            return Results.NotFound();

        var legacyPath = Path.Combine(CacheDirectory(configuration, "medals/legacy"), file);
        if (File.Exists(legacyPath))
            return Results.File(legacyPath, "image/png");

        // Today's icon first (fetched and cached as for today's clients), then a smaller copy of it.
        var result = await HandleCachedAssetAsync("medals/client", file, http, configuration, logs, cancellationToken).ConfigureAwait(false);
        var iconPath = Path.Combine(CacheDirectory(configuration, "medals/client"), file);
        if (!File.Exists(iconPath))
            return result;

        var smaller = LegacyMedal(await File.ReadAllBytesAsync(iconPath, cancellationToken).ConfigureAwait(false));
        if (smaller is null)
            return result;
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        var tempPath = legacyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(tempPath, smaller, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, legacyPath, overwrite: true);
        return Results.File(smaller, "image/png");
    }

    /// <summary>A medal icon fitted into the old images' size (centred, transparent around it); null if it isn't an image.</summary>
    internal static byte[]? LegacyMedal(byte[] png)
    {
        // SKBitmap.Decode throws (rather than returning null) when nothing can read the data.
        using var data = SKData.CreateCopy(png);
        using var codec = SKCodec.Create(data);
        if (codec is null)
            return null;
        using var icon = SKBitmap.Decode(codec);
        if (icon is null)
            return null;
        var scale = Math.Min((float)LegacyMedalWidth / icon.Width, (float)LegacyMedalHeight / icon.Height);
        var width = icon.Width * scale;
        var height = icon.Height * scale;
        using var surface = SKSurface.Create(new SKImageInfo(LegacyMedalWidth, LegacyMedalHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(icon);
        var left = (LegacyMedalWidth - width) / 2;
        var top = (LegacyMedalHeight - height) / 2;
        surface.Canvas.DrawImage(image, new SKRect(left, top, left + width, top + height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>A file under a managed folder, at any depth (<see cref="AssetStore.IsImagePath"/> keeps the path plain).</summary>
    private static async Task<IResult> HandleNestedAssetAsync(
        string relative, IHttpClientFactory httpClientFactory, IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        if (!AssetStore.IsImagePath(relative))
            return Results.NotFound();
        var type = Path.GetExtension(relative).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/png",
        };
        var localPath = Path.Combine(CacheDirectory(configuration, string.Empty), relative);
        if (File.Exists(localPath))
            return Results.File(localPath, type);

        try
        {
            using var http = httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.GetAsync($"{UpstreamBaseUrl}/{relative}", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Results.NotFound();
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            var tempPath = $"{localPath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, localPath, overwrite: true);
            return Results.File(bytes, type);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            loggerFactory.CreateLogger("TomYum.Assets").LogWarning(ex, "Asset '{Path}' could not be fetched from upstream", relative);
            return Results.NotFound();
        }
    }

    private static async Task<IResult> HandleCachedAssetAsync(
        string folder,
        string file,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // Reject anything that isn't a bare `*.png` filename — this is the path-traversal guard.
        if (!IsSafeMedalFile(file))
        {
            return Results.NotFound();
        }

        // Root the cache dir: Results.File needs an absolute path, and IsSafeMedalFile has already
        // guaranteed `file` is a bare filename, so the combined path can't escape the cache directory.
        var cacheDir = CacheDirectory(configuration, folder);
        var localPath = Path.Combine(cacheDir, file);

        // Cache hit: serve straight from disk.
        if (File.Exists(localPath))
        {
            return Results.File(localPath, "image/png");
        }

        var logger = loggerFactory.CreateLogger("TomYum.Assets");
        try
        {
            using var http = httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.GetAsync($"{UpstreamBaseUrl}/{folder}/{file}", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Unknown icon or badge (e.g. a not-yet-hosted custom medal) — 404, don't cache the miss.
                return Results.NotFound();
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            // Cache atomically: write to a temp file then move, so a partial download is never served.
            Directory.CreateDirectory(cacheDir);
            var tempPath = Path.Combine(cacheDir, $"{file}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, localPath, overwrite: true);

            return Results.File(bytes, "image/png");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Asset '{Folder}/{File}' could not be fetched from upstream", folder, file);
            return Results.NotFound();
        }
    }
}
