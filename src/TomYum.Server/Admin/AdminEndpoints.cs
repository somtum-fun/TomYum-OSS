using System.Diagnostics;
using System.Globalization;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;

namespace TomYum.Server.Admin;

/// <summary>
/// The admin API that osu-web's staff panels call (<c>/_admin/*</c>). Every route requires a signed
/// request (<see cref="AdminAuthFilter"/>) and is bound to internal host names only, so the public
/// subdomains Caddy forwards never reach it.
/// </summary>
/// <summary>Body of <c>POST /_admin/beatmaps/import</c>: a beatmap id or a <c>.osu</c> checksum.</summary>
public sealed record BeatmapImportRequest(int? BeatmapId, string? Checksum);

/// <summary>Body of <c>POST /_admin/beatmapsets/{id}/status</c>; no beatmap ids = the whole set.</summary>
public sealed record StatusChangeRequest(string? Status, IReadOnlyList<int>? BeatmapIds);

/// <summary>Body of <c>POST /_admin/users/{id}/wipe-scores</c>; no mode = every mode.</summary>
public sealed record WipeScoresRequest(int? Mode, string? Reason);

/// <summary>Body of <c>POST /_admin/log</c>: an action the website did itself (restrict, silence, …).</summary>
public sealed record StaffLogRequest(string? Action, long? TargetId, string? Summary, string? Result);

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
        admin.MapPost("/beatmaps/import", ImportBeatmapAsync);
        admin.MapGet("/beatmapsets/{setId:int}", GetSetAsync);
        admin.MapPost("/beatmapsets/{setId:int}/status", ChangeStatusAsync);
        admin.MapPost("/beatmapsets/import-bulk", StartBulkImport);
        admin.MapGet("/beatmapsets/import-jobs", (BeatmapImportRunner runner) => Results.Ok(runner.Recent));
        admin.MapGet("/beatmapsets/import-jobs/{id}", (string id, BeatmapImportRunner runner) =>
            runner.Get(id) is { } job ? Results.Ok(job) : Results.NotFound());

        admin.MapGet("/log", async (IStaffLogRepository log, string? action, long? targetId, string? jobId, long? before, int? limit, CancellationToken cancellationToken) =>
            Results.Ok(await log.ListAsync(new StaffLogQuery(action, targetId, jobId, before, limit ?? 50), cancellationToken).ConfigureAwait(false)));
        admin.MapPost("/log", AppendLogAsync);

        // Moderation (GMT/Admin panels).
        admin.MapPost("/users/{userId:int}/refresh", async (int userId, UserModerationSync sync, CancellationToken cancellationToken) =>
            await sync.RefreshAsync(userId, cancellationToken).ConfigureAwait(false) is { } result ? Results.Ok(result) : Results.NotFound());
        admin.MapGet("/users/{userId:int}/scores", async (int userId, int? mode, bool? best, int? limit, ScoreModeration scores, CancellationToken cancellationToken) =>
            Results.Ok(await scores.ListAsync(userId, mode, best ?? false, limit ?? 50, cancellationToken).ConfigureAwait(false)));
        admin.MapPost("/users/{userId:int}/wipe-scores", async (int userId, WipeScoresRequest request, ScoreModeration scores, HttpContext context, CancellationToken cancellationToken) =>
            Results.Ok(await scores.WipeAsync(userId, request.Mode, ActorId(context), request.Reason, cancellationToken).ConfigureAwait(false)));
        admin.MapDelete("/scores/{scoreId:long}", async (long scoreId, string? reason, ScoreModeration scores, HttpContext context, CancellationToken cancellationToken) =>
            await scores.RemoveAsync(scoreId, ActorId(context), reason, cancellationToken).ConfigureAwait(false) is { } result ? Results.Ok(result) : Results.NotFound());
        // Replay of a website score (osu-web's replay download fetches it from here).
        admin.MapGet("/replays/website-score/{osuWebScoreId:long}", async (long osuWebScoreId, OsuWebScoreMirror mirror, IConfiguration configuration, CancellationToken cancellationToken) =>
            await mirror.GetTomYumScoreIdAsync((ulong)osuWebScoreId, cancellationToken).ConfigureAwait(false) is { } scoreId
                && ReplayFiles.Exists(configuration, scoreId)
                ? Results.File(ReplayFiles.PathFor(configuration, scoreId), "application/octet-stream")
                : Results.NotFound());
        admin.MapGet("/chat",async (int? userId, string? target, long? before, int? limit, IChatLogRepository chat, CancellationToken cancellationToken) =>
            Results.Ok(await chat.ListAsync(new ChatLogQuery(userId, target, before, limit ?? 100), cancellationToken).ConfigureAwait(false)));

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

    private static async Task<IResult> AppendLogAsync(StaffLogRequest request, IStaffLogRepository log, TimeProvider time, HttpContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Action) || request.Action.Length > 64 || string.IsNullOrWhiteSpace(request.Summary))
            return Results.BadRequest(new { error = "Give an action (up to 64 characters) and a summary." });

        var summary = request.Summary.Trim();
        await log.AddAsync(new Domain.Entities.StaffLogEntry
        {
            CreatedAt = time.GetUtcNow().UtcDateTime,
            ActorId = ActorId(context),
            Action = request.Action.Trim(),
            TargetId = request.TargetId,
            Summary = summary.Length > 512 ? summary[..512] : summary,
            Result = request.Result is { Length: > 32 } result ? result[..32] : request.Result,
        }, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>
    /// Imports the set of a beatmap the website doesn't have yet, found by beatmap id or <c>.osu</c>
    /// checksum (osu-web calls this when lazer asks about a map, docs/lazer.md). 404 when osu! doesn't
    /// know it either.
    /// </summary>
    private static async Task<IResult> ImportBeatmapAsync(
        BeatmapImportRequest request, IBeatmapRepository beatmaps, BeatmapService service, IOsuApiClient api, BeatmapSetImporter importer,
        HttpContext context, CancellationToken cancellationToken)
    {
        int? setId = null;
        if (request.BeatmapId is > 0 and var id)
        {
            setId = (await beatmaps.GetByIdAsync(id, cancellationToken).ConfigureAwait(false))?.SetId
                ?? (await api.GetByIdAsync(id, cancellationToken).ConfigureAwait(false))?.SetId;
        }
        else if (!string.IsNullOrWhiteSpace(request.Checksum))
        {
            setId = (await beatmaps.GetByMd5Async(request.Checksum, cancellationToken).ConfigureAwait(false))?.SetId
                ?? (await service.GetByMd5Async(request.Checksum, cancellationToken).ConfigureAwait(false))?.SetId;
        }
        else
        {
            return Results.BadRequest(new { error = "Give a beatmap id or checksum." });
        }

        // Custom (server-only) sets are on the website already; there's nothing on osu! to import.
        if (setId is not { } set || set <= 0 || set >= TomYum.Domain.Entities.Beatmap.CustomIdFloor)
            return Results.NotFound(new { error = "osu! doesn't know that beatmap." });

        var result = await importer.ImportAsync(set, ActorId(context), cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Found ? Results.Ok(result) : Results.NotFound(result);
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
        if (string.Equals(request.Status, BeatmapStatusChanger.UnlockName, StringComparison.OrdinalIgnoreCase))
        {
            var unlocked = await changer.UnlockAsync(setId, request.BeatmapIds, ActorId(context), cancellationToken).ConfigureAwait(false);
            return unlocked is null ? Results.NotFound(new { error = "That set isn't on the server." }) : Results.Ok(unlocked);
        }

        if (!BeatmapStatusChanger.Statuses.TryGetValue(request.Status ?? string.Empty, out var status))
            return Results.BadRequest(new { error = "Unknown status; use ranked, approved, qualified, loved, pending or bancho." });

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
