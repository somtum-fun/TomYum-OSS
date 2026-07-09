using System.Text.RegularExpressions;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// Serves static osu! assets on the <c>assets.&lt;domain&gt;</c> host. The devserver-patched client
/// rewrites <c>assets.ppy.sh</c> to <c>assets.&lt;domain&gt;</c>, so medal-unlock popups request their
/// icons from here (<c>/medals/client/{file}.png</c> and the <c>@2x</c> retina variant).
/// </summary>
/// <remarks>
/// Medal icons are cache-and-served: on first request we fetch the file from ppy's CDN, cache it to
/// disk (<c>Assets:CachePath</c>/<c>medals/client</c>, default <c>data/assets/medals/client</c>), and
/// serve every later request from the cache — no repeated CDN hits, and the same on-disk layout that
/// server-hosted custom medals will drop into later. (The beatmap-thumbnail host, by contrast, just
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
        app.MapGet("/medals/client/{file}", HandleMedalAssetAsync).RequireHost(hosts);
    }

    private static async Task<IResult> HandleMedalAssetAsync(
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
        var cacheDir = Path.GetFullPath(Path.Combine(configuration[CachePathKey] ?? DefaultCachePath, "medals", "client"));
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
            using var response = await http.GetAsync($"{UpstreamBaseUrl}/medals/client/{file}", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Unknown medal icon (e.g. a not-yet-hosted custom medal) — 404, don't cache the miss.
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
            logger.LogWarning(ex, "Medal asset '{File}' could not be fetched from upstream", file);
            return Results.NotFound();
        }
    }
}
