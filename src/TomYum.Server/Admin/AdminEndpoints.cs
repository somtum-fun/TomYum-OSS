using System.Diagnostics;
using System.Globalization;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;

namespace TomYum.Server.Admin;

/// <summary>
/// The admin API that osu-web's staff panels call (<c>/_admin/*</c>). Every route requires a signed
/// request (<see cref="AdminAuthFilter"/>) and is bound to internal host names only, so the public
/// subdomains Caddy forwards never reach it.
/// </summary>
/// <summary>Body of <c>POST /_admin/beatmapsets/{id}/status</c>; no beatmap ids = the whole set.</summary>
public sealed record StatusChangeRequest(string? Status, IReadOnlyList<int>? BeatmapIds);

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
        admin.MapGet("/beatmapsets/{setId:int}", GetSetAsync);
        admin.MapPost("/beatmapsets/{setId:int}/status", ChangeStatusAsync);
        admin.MapPost("/beatmapsets/import-bulk", StartBulkImport);
        admin.MapGet("/beatmapsets/import-jobs", (BeatmapImportRunner runner) => Results.Ok(runner.Recent));
        admin.MapGet("/beatmapsets/import-jobs/{id}", (string id, BeatmapImportRunner runner) =>
            runner.Get(id) is { } job ? Results.Ok(job) : Results.NotFound());

        admin.MapGet("/log", async (IStaffLogRepository log, string? action, long? targetId, string? jobId, long? before, int? limit, CancellationToken cancellationToken) =>
            Results.Ok(await log.ListAsync(new StaffLogQuery(action, targetId, jobId, before, limit ?? 50), cancellationToken).ConfigureAwait(false)));

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

    /// <summary>The acting staff member's osu-web user id (0 when the request didn't say).</summary>
    public static int ActorId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return int.TryParse(context.Items[AdminAuthFilter.ActorItemKey] as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : 0;
    }

    private static async Task<IResult> ImportSetAsync(int setId, BeatmapSetImporter importer, HttpContext context, CancellationToken cancellationToken)
    {
        var result = await importer.ImportAsync(setId, ActorId(context), cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Found ? Results.Ok(result) : Results.NotFound(result);
    }

    /// <summary>The set's difficulties as the game server sees them (status, and whether staff set it).</summary>
    private static async Task<IResult> GetSetAsync(int setId, IBeatmapRepository beatmaps, BeatmapService service, CancellationToken cancellationToken)
    {
        var set = await beatmaps.GetBySetAsync(setId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(set.Select(b => new
        {
            b.Id,
            b.Version,
            b.Mode,
            b.StarRating,
            Status = StatusName(service.GetStatusOverride(b.Id) ?? b.Status),
            StaffSet = b.StatusFrozen,
        }));
    }

    private static async Task<IResult> ChangeStatusAsync(int setId, StatusChangeRequest request, BeatmapStatusChanger changer, HttpContext context, CancellationToken cancellationToken)
    {
        if (!BeatmapStatusChanger.Statuses.TryGetValue(request.Status ?? string.Empty, out var status))
            return Results.BadRequest(new { error = "Unknown status; use ranked, approved, qualified, loved or pending." });

        var result = await changer.ChangeAsync(setId, status, request.BeatmapIds, ActorId(context), cancellationToken).ConfigureAwait(false);
        return result is null ? Results.NotFound(new { error = "That set isn't on the server or on osu!." }) : Results.Ok(result);
    }

    private static string StatusName(RankedStatus status) => status switch
    {
        RankedStatus.Ranked => "ranked",
        RankedStatus.Approved => "approved",
        RankedStatus.Qualified => "qualified",
        RankedStatus.Loved => "loved",
        RankedStatus.NotSubmitted => "not_submitted",
        _ => "pending",
    };

    private static IResult StartBulkImport(BulkImportRequest request, BeatmapImportRunner runner, HttpContext context)
    {
        if (request.RankedFrom is null && (request.SetIds is null || request.SetIds.Count == 0))
            return Results.BadRequest(new { error = "Give set ids or a ranked-from date." });
        if (request.RankedFrom is { } from && request.RankedTo is { } to && to < from)
            return Results.BadRequest(new { error = "The end date is before the start date." });

        var job = runner.Enqueue(request, ActorId(context));
        return Results.Accepted($"/_admin/beatmapsets/import-jobs/{job.Id}", job);
    }
}
