using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Server.Beatmaps;

namespace TomYum.Server.Admin;

/// <summary>A main-menu banner to add: its image's link, where clicking it goes, and optionally when it shows.</summary>
public sealed record MenuBannerRequest(string Image, string Url, DateTimeOffset? Begins = null, DateTimeOffset? Expires = null);

/// <summary>A banner to remove, by its place in the list (0 = first).</summary>
public sealed record MenuBannerRemoveRequest(int Index);

/// <summary>
/// lazer's main-menu banners for the website's admin panel (Admin panel › Menu banners), the same
/// as Discord's /banner_add and /banner_remove (<see cref="MenuBanners"/>).
/// </summary>
public static class AdminMenuBannerEndpoints
{
    public static void MapMenuBanners(this RouteGroupBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        admin.MapGet("/menu-banners", async (MenuBanners banners, CancellationToken cancellationToken) =>
            Results.Ok(await banners.ListAsync(cancellationToken).ConfigureAwait(false)));
        admin.MapPost("/menu-banners", AddAsync);
        admin.MapPost("/menu-banners/remove", RemoveAsync);
    }

    private static async Task<IResult> AddAsync(MenuBannerRequest request, MenuBanners banners, IServiceProvider services, HttpContext context, CancellationToken cancellationToken)
    {
        if (await banners.AddAsync(request.Image, request.Url, request.Begins, request.Expires, cancellationToken).ConfigureAwait(false) is { } problem)
            return Results.BadRequest(new { error = problem });
        await LogAsync(services, context, "menu_banner.add", $"Added a menu banner ({request.Image.Trim()}, linking to {request.Url.Trim()})", cancellationToken).ConfigureAwait(false);
        return Results.Ok();
    }

    private static async Task<IResult> RemoveAsync(MenuBannerRemoveRequest request, MenuBanners banners, IServiceProvider services, HttpContext context, CancellationToken cancellationToken)
    {
        if (await banners.RemoveAsync(request.Index, cancellationToken).ConfigureAwait(false) is not { } removed)
            return Results.NotFound(new { error = "That banner isn't there (anymore)." });
        await LogAsync(services, context, "menu_banner.remove", $"Removed a menu banner ({removed.Image})", cancellationToken).ConfigureAwait(false);
        return Results.Ok();
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
