using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.Admin;

/// <summary>A badge to give: a badge's file name (here or osu!'s) or an image link, its tooltip, and an optional link.</summary>
/// <param name="Image">A badge's file name (here or osu!'s), or a link to an image.</param>
/// <param name="Description">Its tooltip.</param>
/// <param name="Url">Where clicking it goes (optional).</param>
/// <param name="Name">What a link's image is saved as (else the link's file name).</param>
public sealed record BadgeRequest(string Image, string Description, string? Url, string? Name = null);

/// <summary>A badge to take away, by its image name.</summary>
public sealed record BadgeRemoveRequest(string Image);

/// <summary>
/// Profile badges for the website's admin panel (the same as Discord's /addbadge and /removebadge):
/// images are prepared like there (osu!'s badge files, or a link fitted to 86×40 and served here).
/// </summary>
public static class AdminBadgeEndpoints
{
    public static void MapBadges(this RouteGroupBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        admin.MapGet("/users/{userId:int}/badges", async (int userId, IServiceProvider services, CancellationToken cancellationToken) =>
            services.GetService<OsuWebBadges>() is { } badges
                ? Results.Ok(await badges.ListAsync(userId, cancellationToken).ConfigureAwait(false))
                : Results.NotFound(new { error = "Badges need the website." }));
        admin.MapPost("/users/{userId:int}/badges", AddAsync);
        admin.MapPost("/users/{userId:int}/badges/remove", RemoveAsync);
    }

    private static async Task<IResult> AddAsync(int userId, BadgeRequest request, IServiceProvider services, HttpContext context, CancellationToken cancellationToken)
    {
        if (services.GetService<OsuWebBadges>() is not { } badges)
            return Results.NotFound(new { error = "Badges need the website." });
        if (await services.GetRequiredService<IUserRepository>().GetByIdAsync(userId, cancellationToken).ConfigureAwait(false) is not { } target)
            return Results.NotFound(new { error = "No such player." });

        var description = request.Description?.Trim() ?? string.Empty;
        if (description.Length == 0)
            return Results.BadRequest(new { error = "Give the badge's description (its tooltip)." });
        var url = request.Url?.Trim();
        if (!string.IsNullOrEmpty(url) && !(Uri.TryCreate(url, UriKind.Absolute, out var link) && (link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp)))
            return Results.BadRequest(new { error = "The link should be a web address (https://…)." });

        var (image, problem) = await services.GetRequiredService<BadgeImages>().PrepareAsync(request.Image ?? string.Empty, request.Name, cancellationToken).ConfigureAwait(false);
        if (image is null)
            return Results.BadRequest(new { error = problem });
        if (!await badges.AddAsync(target.Id, image, description, url, cancellationToken).ConfigureAwait(false))
            return Results.Conflict(new { error = $"{target.Username} already has that badge." });

        await LogAsync(services, context, "user.badge_add", target, $"Gave {target.Username} the badge \"{description}\" ({image})", cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { image });
    }

    private static async Task<IResult> RemoveAsync(int userId, BadgeRemoveRequest request, IServiceProvider services, HttpContext context, CancellationToken cancellationToken)
    {
        if (services.GetService<OsuWebBadges>() is not { } badges)
            return Results.NotFound(new { error = "Badges need the website." });
        if (await services.GetRequiredService<IUserRepository>().GetByIdAsync(userId, cancellationToken).ConfigureAwait(false) is not { } target)
            return Results.NotFound(new { error = "No such player." });
        var badge = (await badges.ListAsync(userId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(b => b.Image == request.Image);
        if (badge is null)
            return Results.NotFound(new { error = "They don't have that badge." });

        await badges.RemoveAsync(userId, badge.Image, cancellationToken).ConfigureAwait(false);
        await LogAsync(services, context, "user.badge_remove", target, $"Took the badge \"{badge.Description}\" ({badge.Image}) from {target.Username}", cancellationToken).ConfigureAwait(false);
        return Results.Ok();
    }

    private static Task LogAsync(IServiceProvider services, HttpContext context, string action, User target, string summary, CancellationToken cancellationToken) =>
        services.GetRequiredService<IStaffLogRepository>().AddAsync(new StaffLogEntry
        {
            CreatedAt = DateTime.UtcNow,
            ActorId = AdminEndpoints.ActorId(context),
            Action = action,
            TargetId = target.Id,
            Summary = summary.Length > 512 ? summary[..512] : summary,
        }, cancellationToken);
}
