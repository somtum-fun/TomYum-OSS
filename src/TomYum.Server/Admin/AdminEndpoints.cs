using System.Diagnostics;
using TomYum.Server.Bancho;

namespace TomYum.Server.Admin;

/// <summary>
/// The admin API that osu-web's staff panels call (<c>/_admin/*</c>). Every route requires a signed
/// request (<see cref="AdminAuthFilter"/>) and is bound to internal host names only, so the public
/// subdomains Caddy forwards never reach it.
/// </summary>
public static class AdminEndpoints
{
    /// <summary>Host names the admin API answers on by default (compose service name + local).</summary>
    public static readonly string[] DefaultHosts = ["server", "localhost", "127.0.0.1"];

    /// <summary>
    /// Makes admin API request bodies re-readable. Minimal APIs bind the JSON body before endpoint
    /// filters run, so <see cref="AdminAuthFilter"/> can only read it for the signature if it was
    /// buffered first. Register before the endpoints run.
    /// </summary>
    public static IApplicationBuilder UseAdminRequestBuffering(this IApplicationBuilder app) =>
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/_admin", StringComparison.Ordinal),
            branch => branch.Use((context, next) =>
            {
                context.Request.EnableBuffering();
                return next(context);
            }));

    public static void MapAdmin(this IEndpointRouteBuilder app, params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(app);

        var admin = app.MapGroup("/_admin")
            .RequireHost(hosts)
            .AddEndpointFilter<AdminAuthFilter>();

        admin.MapPost("/beatmapsets/{setId:int}/import", ImportSetAsync);
        admin.MapPost("/beatmapsets/import-bulk", StartBulkImport);
        admin.MapGet("/beatmapsets/import-jobs", (BeatmapImportRunner runner) => Results.Ok(runner.Recent));
        admin.MapGet("/beatmapsets/import-jobs/{id}", (string id, BeatmapImportRunner runner) =>
            runner.Get(id) is { } job ? Results.Ok(job) : Results.NotFound());

        admin.MapGet("/online", (PlayerRegistry players) => Results.Ok(
            players.All
                .Where(p => p.UserId != BanchoBot.UserId)
                .OrderBy(p => p.Presence.Username, StringComparer.OrdinalIgnoreCase)
                .Select(p => new
                {
                    p.UserId,
                    p.Presence.Username,
                    p.Presence.Mode,
                    p.Presence.Action,
                    p.Presence.ActionInfo,
                    p.Presence.BeatmapId,
                    LastActivity = p.LastActivityUtc,
                })));

        admin.MapGet("/stats", (PlayerRegistry players, TomYumVersion version) => Results.Ok(new
        {
            Version = version.Display,
            Online = players.All.Count(p => p.UserId != BanchoBot.UserId),
            StartedAt = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero),
        }));
    }

    private static async Task<IResult> ImportSetAsync(int setId, BeatmapSetImporter importer, CancellationToken cancellationToken)
    {
        var result = await importer.ImportAsync(setId, cancellationToken).ConfigureAwait(false);
        return result.Found ? Results.Ok(result) : Results.NotFound(result);
    }

    private static IResult StartBulkImport(BulkImportRequest request, BeatmapImportRunner runner, HttpContext context)
    {
        if (request.RankedFrom is null && (request.SetIds is null || request.SetIds.Count == 0))
            return Results.BadRequest(new { error = "Give set ids or a ranked-from date." });
        if (request.RankedFrom is { } from && request.RankedTo is { } to && to < from)
            return Results.BadRequest(new { error = "The end date is before the start date." });

        var actor = context.Items[AdminAuthFilter.ActorItemKey] as string ?? string.Empty;
        var job = runner.Enqueue(request, actor);
        return Results.Accepted($"/_admin/beatmapsets/import-jobs/{job.Id}", job);
    }
}
