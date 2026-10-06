using Microsoft.EntityFrameworkCore;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// Beatmap pack downloads (<see cref="PackArchives"/>): <c>/d/packs/{tag}</c> for players (the website's
/// pack pages link it), and the admin API's <c>/packs/{tag}/download</c> for the staff panel to see or
/// start a build.
/// </summary>
public static class PackEndpoints
{
    /// <summary>
    /// <c>GET /d/packs/{tag}</c>: the pack's zip, or (while it's being put together) a friendly 503 that
    /// starts the build. No host restriction, like <c>/d/{set}</c>. Hidden packs aren't downloadable.
    /// </summary>
    public static void MapPackDownloads(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/d/packs/{tag}", DownloadAsync);
    }

    /// <summary>The admin API's pack routes: <c>GET</c> where a pack's zip is, <c>POST</c> builds it.</summary>
    public static void MapPackAdmin(this RouteGroupBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        admin.MapGet("/packs/{tag}/download", async (string tag, OsuWebDbContext db, PackArchives archives, CancellationToken cancellationToken) =>
            await SetIdsAsync(db, tag, includeHidden: true, cancellationToken).ConfigureAwait(false) is { } pack
                ? Results.Ok(archives.Status(pack.Tag, pack.SetIds))
                : Results.NotFound(new { error = "There's no pack with that tag." }));
        admin.MapPost("/packs/{tag}/download", async (string tag, OsuWebDbContext db, PackArchives archives, CancellationToken cancellationToken) =>
            await SetIdsAsync(db, tag, includeHidden: true, cancellationToken).ConfigureAwait(false) is { } pack
                ? Results.Ok(archives.Start(pack.Tag, pack.SetIds))
                : Results.NotFound(new { error = "There's no pack with that tag." }));
    }

    private static async Task<IResult> DownloadAsync(string tag, HttpContext context, OsuWebDbContext db, PackArchives archives, CancellationToken cancellationToken)
    {
        if (await SetIdsAsync(db, tag, includeHidden: false, cancellationToken).ConfigureAwait(false) is not { } pack || pack.SetIds.Count == 0)
            return Results.NotFound();

        // Only packs whose download is this server's are built (staff may have pasted a link instead).
        if (!IsBuiltHere(pack.Url, pack.Tag))
        {
            return Uri.TryCreate(pack.Url, UriKind.Absolute, out var link) && (link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp)
                ? Results.Redirect(link.AbsoluteUri)
                : Results.NotFound();
        }

        if (archives.ReadyPath(pack.Tag, pack.SetIds) is { } path)
            return Results.File(path, "application/zip", DownloadName(pack.Tag, pack.Name), enableRangeProcessing: true);

        // A failed build isn't started again on every player's request; the panel's "Build it now" can.
        var status = archives.Start(pack.Tag, pack.SetIds, PackArchives.FailedRetryDelay);
        context.Response.Headers.RetryAfter = "120";
        context.Response.Headers.CacheControl = "no-store";
        var message = status.State == "failed"
            ? "This pack's download couldn't be put together just now. Please try again in a few minutes."
            : $"This pack's download is being put together ({status.Done} of {status.Total} sets so far). Please try again in a few minutes.";
        return Results.Text(message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>"S1 - Beatmap Pack #1.zip", without characters a file name can't have.</summary>
    internal static string DownloadName(string tag, string name)
    {
        var clean = new string(name.Where(c => !char.IsControl(c) && !"\"*:<>?/\\|".Contains(c, StringComparison.Ordinal)).ToArray()).Trim();
        return clean.Length == 0 ? $"{tag}.zip" : $"{tag} - {clean}.zip";
    }

    /// <summary>Whether a pack's download link is this server's <c>/d/packs/{tag}</c> (on whatever host the website uses).</summary>
    internal static bool IsBuiltHere(string? url, string tag) =>
        url is not null && url.EndsWith("/d/packs/" + Uri.EscapeDataString(tag), StringComparison.OrdinalIgnoreCase);

    private static async Task<(string Tag, string Name, string Url, IReadOnlyCollection<int> SetIds)?> SetIdsAsync(
        OsuWebDbContext db, string tag, bool includeHidden, CancellationToken cancellationToken)
    {
        if (!PackArchives.IsValidTag(tag))
            return null;
        var pack = await db.BeatmapPacks.AsNoTracking().FirstOrDefaultAsync(p => p.Tag == tag, cancellationToken).ConfigureAwait(false);
        if (pack is null || (pack.Hidden && !includeHidden))
            return null;
        var packId = (ushort)pack.PackId;
        var sets = await db.BeatmapPackItems.AsNoTracking()
            .Where(i => i.PackId == packId)
            .Select(i => (int)i.BeatmapsetId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // The file names use the stored tag (the lookup ignores case).
        return (pack.Tag, pack.Name, pack.Url, sets.Distinct().ToList());
    }
}
