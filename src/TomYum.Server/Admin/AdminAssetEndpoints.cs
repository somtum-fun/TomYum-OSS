using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Beatmaps;

namespace TomYum.Server.Admin;

/// <summary>A pair to save: its path (the normal one's) and either image, base64.</summary>
public sealed record AssetSaveRequest(string Path, string? Normal, string? Retina);

/// <summary>A pair, by its path (the normal one's).</summary>
public sealed record AssetPathRequest(string Path);

/// <summary>A pair and its new name (in the same folder).</summary>
public sealed record AssetRenameRequest(string Path, string Name);

/// <summary>osu! asset links (or paths) to copy here, one per line.</summary>
public sealed record AssetImportRequest(string Links);

/// <summary>
/// The assets host's images for the website's admin panel (Admin panel › Assets): badges, tournament
/// banners and medal icons as normal/@2x pairs at osu!'s paths (<see cref="AssetStore"/>).
/// </summary>
public static class AdminAssetEndpoints
{
    public static void MapAssetAdmin(this RouteGroupBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        admin.MapGet("/assets", (string? folder, AssetStore store) =>
            store.List(folder ?? AssetsEndpoints.BadgeFolder) is { } listing
                ? Results.Ok(new { roots = AssetStore.Roots, folder = listing })
                : Results.NotFound(new { error = "That isn't one of the asset folders." }));
        admin.MapPost("/assets", SaveAsync);
        admin.MapPost("/assets/remove", RemoveAsync);
        admin.MapPost("/assets/rename", RenameAsync);
        admin.MapPost("/assets/import", ImportAsync);
    }

    private static async Task<IResult> SaveAsync(AssetSaveRequest request, AssetStore store, IServiceProvider services, HttpContext context, CancellationToken cancellationToken)
    {
        byte[]? Decode(string? base64) => string.IsNullOrEmpty(base64) ? null : Convert.FromBase64String(base64);
        string? problem;
        try
        {
            problem = await store.SaveAsync(request.Path ?? string.Empty, Decode(request.Normal), Decode(request.Retina), cancellationToken).ConfigureAwait(false);
        }
        catch (FormatException)
        {
            problem = "The image didn't arrive whole.";
        }
        if (problem is not null)
            return Results.BadRequest(new { error = problem });

        await LogAsync(services, context, "asset.save", $"Saved {request.Path} (normal{(request.Retina is null ? string.Empty : " and @2x")})", cancellationToken).ConfigureAwait(false);
        return Results.Ok(store.Pair(request.Path!));
    }

    private static async Task<IResult> RemoveAsync(AssetPathRequest request, AssetStore store, IServiceProvider services, HttpContext context, CancellationToken cancellationToken)
    {
        if (!store.Delete(request.Path ?? string.Empty))
            return Results.NotFound(new { error = "That image isn't there." });
        await LogAsync(services, context, "asset.remove", $"Removed {request.Path} (both sizes)", cancellationToken).ConfigureAwait(false);
        return Results.Ok();
    }

    /// <summary>Renames a pair, and what uses it follows: badges with that image, and banners of that name in that folder.</summary>
    private static async Task<IResult> RenameAsync(AssetRenameRequest request, AssetStore store, IServiceProvider services, HttpContext context, CancellationToken cancellationToken)
    {
        var path = request.Path ?? string.Empty;
        if (store.Rename(path, request.Name ?? string.Empty) is { } problem)
            return Results.BadRequest(new { error = problem });

        var extension = Path.GetExtension(path);
        var name = (request.Name ?? string.Empty).Trim();
        if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            name = name[..^extension.Length];
        var folder = Path.GetDirectoryName(path)!.Replace('\\', '/');
        var target = $"{folder}/{name}{extension}";

        var followed = 0;
        if (services.GetService<OsuWebDbContext>() is { } web && target != path)
        {
            if (folder == AssetsEndpoints.BadgeFolder)
            {
                followed = await web.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE osu_badges SET image = {name + extension} WHERE image = {Path.GetFileName(path)}", cancellationToken).ConfigureAwait(false);
            }
            else if (folder.StartsWith(AssetStore.BannerFolder + "/", StringComparison.Ordinal) && extension == ".jpg")
            {
                // A banner is its tournament's prefix (…/tournament-banners/{folder}/) and the name before .jpg.
                var prefix = "%/" + folder + "/";
                followed = await web.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE osu_profile_banners p JOIN tournament_banners b ON b.tournament_id = p.tournament_id
                    SET p.country_acronym = {name}
                    WHERE b.banner_url_prefix LIKE {prefix} AND p.country_acronym = {Path.GetFileNameWithoutExtension(path)}
                    """, cancellationToken).ConfigureAwait(false);
            }
        }

        await LogAsync(services, context, "asset.rename", $"Renamed {path} to {target}" + (followed > 0 ? $" ({followed} using it follow)" : string.Empty), cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { path = target, followed });
    }

    private static async Task<IResult> ImportAsync(AssetImportRequest request, AssetStore store, IServiceProvider services, HttpContext context, CancellationToken cancellationToken)
    {
        var links = (request.Links ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Take(100)
            .ToList();
        if (links.Count == 0)
            return Results.BadRequest(new { error = "Paste one or more osu! asset links (one per line)." });

        var copied = new List<string>();
        var problems = new List<string>();
        foreach (var link in links)
        {
            var (path, problem) = await store.ImportAsync(link, cancellationToken).ConfigureAwait(false);
            if (path is not null)
                copied.Add(path);
            else
                problems.Add(problem!);
        }
        if (copied.Count > 0)
            await LogAsync(services, context, "asset.import", $"Copied from osu!: {string.Join(", ", copied)}", cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { copied, problems });
    }

    private static Task LogAsync(IServiceProvider services, HttpContext context, string action, string summary, CancellationToken cancellationToken) =>
        services.GetRequiredService<IStaffLogRepository>().AddAsync(new StaffLogEntry
        {
            CreatedAt = DateTime.UtcNow,
            ActorId = AdminEndpoints.ActorId(context),
            Action = action,
            Summary = summary.Length > 512 ? summary[..509] + "..." : summary,
        }, cancellationToken);
}
