using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Globalization;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;

using TomYum.Server.OsuWeb;

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
/// <summary>Body of <c>POST /_admin/users/{id}/wipe-scores</c>: a mode or all, and (a rollback) only scores set after <c>After</c>.</summary>
public sealed record WipeScoresRequest(int? Mode, string? Reason, DateTimeOffset? After = null);

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
        admin.MapBadges();
        admin.MapAssetAdmin();
        admin.MapMenuBanners();
        admin.MapPackAdmin();
        admin.MapModeStats();
        admin.MapModeScores();
        admin.MapModeRankings();
        admin.MapPpIfRanked();
        admin.MapGet("/users/{userId:int}/pp-history/{mode:int}", PpHistoryJob.GetAsync);
        admin.MapAccounts();
        TomYum.Server.LastFm.LastFmEndpoints.MapLastFm(admin);
        admin.MapPost("/beatmaps/import", ImportBeatmapAsync);
        admin.MapGet("/beatmapsets/{setId:int}", GetSetAsync);
        admin.MapPost("/beatmapsets/{setId:int}/status", ChangeStatusAsync);
        admin.MapPost("/beatmapsets/import-bulk", StartBulkImport);
        admin.MapGet("/beatmapsets/import-jobs", (BeatmapImportRunner runner) => Results.Ok(runner.Recent));
        admin.MapGet("/beatmapsets/import-jobs/{id}", (string id, BeatmapImportRunner runner) =>
            runner.Get(id) is { } job ? Results.Ok(job) : Results.NotFound());

        // The staff log: by action (or "user." for all of a kind), player, who did it, job; paged with before.
        admin.MapGet("/log", async (IStaffLogRepository log, string? action, long? targetId, string? jobId, long? before, int? limit, long? actorId, CancellationToken cancellationToken) =>
            Results.Ok(await log.ListAsync(new StaffLogQuery(action, targetId, jobId, before, limit ?? 50, actorId), cancellationToken).ConfigureAwait(false)));
        admin.MapPost("/log", AppendLogAsync);
        // Maintenance mode and in-game alerts (the admin panel).
        admin.MapGet("/maintenance", (Maintenance maintenance) => Results.Ok(MaintenanceJson(maintenance)));
        admin.MapPost("/maintenance", SetMaintenanceAsync);
        admin.MapPost("/alert", SendAlertAsync);
        // pp reworks: their progress, starting one (the modes, the library and versions it's for, why), cancelling one.
        admin.MapGet("/pp-reworks", async (PpReworks reworks, CancellationToken cancellationToken) =>
            Results.Ok(await reworks.StatusAsync(cancellationToken).ConfigureAwait(false)));
        admin.MapPost("/pp-reworks", StartPpReworkAsync);
        // The pp libraries (the host's pp updater): versions, checking for newer ones, updating one.
        admin.MapGet("/pp-libraries", async (PpLibraries libraries) => Results.Ok(await libraries.StatusAsync().ConfigureAwait(false)));
        admin.MapPost("/pp-libraries/check", async (PpLibraries libraries) =>
            await libraries.CheckAsync().ConfigureAwait(false) ? Results.Ok() : Results.BadRequest(new { error = "The pp updater isn't set up on this server." }));
        admin.MapPost("/pp-libraries/update", UpdatePpLibraryAsync);
        admin.MapPost("/pp-reworks/{id:int}/cancel", async (int id, PpReworks reworks, HttpContext context, CancellationToken cancellationToken) =>
            await reworks.CancelAsync(id, ActorId(context), cancellationToken).ConfigureAwait(false) ? Results.Ok() : Results.NotFound());

        // Moderation (GMT/Admin panels).
        admin.MapPost("/users/{userId:int}/refresh", async (int userId, UserModerationSync sync, CancellationToken cancellationToken) =>
            await sync.RefreshAsync(userId, cancellationToken).ConfigureAwait(false) is { } result ? Results.Ok(result) : Results.NotFound());
        admin.MapGet("/users/{userId:int}/scores", async (int userId, int? mode, bool? best, int? limit, ScoreModeration scores, CancellationToken cancellationToken) =>
            Results.Ok(await scores.ListAsync(userId, mode, best ?? false, limit ?? 50, cancellationToken).ConfigureAwait(false)));
        admin.MapPost("/users/{userId:int}/wipe-scores", async (int userId, WipeScoresRequest request, ScoreModeration scores, HttpContext context, CancellationToken cancellationToken) =>
            Results.Ok(await scores.WipeAsync(userId, request.Mode, ActorId(context), request.Reason, request.After, cancellationToken).ConfigureAwait(false)));
        // Every score's pp again (after a pp algorithm update): same as a status change does per map.
        admin.MapPost("/scores/recalculate", RecalculateAllScoresAsync);
        admin.MapDelete("/scores/{scoreId:long}", async (long scoreId, string? reason, ScoreModeration scores, HttpContext context, CancellationToken cancellationToken) =>
            await scores.RemoveAsync(scoreId, ActorId(context), reason, cancellationToken).ConfigureAwait(false) is { } result ? Results.Ok(result) : Results.NotFound());
        admin.MapPost("/scores/restore", RestoreScoreAsync);
        // Replay of a website score (osu-web's replay download fetches it from here).
        admin.MapGet("/replays/website-score/{osuWebScoreId:long}", async (long osuWebScoreId, OsuWebScoreMirror mirror, IConfiguration configuration, CancellationToken cancellationToken) =>
            await mirror.GetTomYumScoreIdAsync((ulong)osuWebScoreId, cancellationToken).ConfigureAwait(false) is { } scoreId
                && ReplayFiles.Exists(configuration, scoreId)
                ? Results.File(ReplayFiles.PathFor(configuration, scoreId), "application/octet-stream")
                : Results.NotFound());
        admin.MapGet("/chat",async (int? userId, string? target, long? before, int? limit, IChatLogRepository chat, CancellationToken cancellationToken) =>
            Results.Ok(await chat.ListAsync(new ChatLogQuery(userId, target, before, limit ?? 100), cancellationToken).ConfigureAwait(false)));

        // Live multiplayer rooms (the website's Tournament Committee panel).
        admin.MapGet("/matches", (MatchModeration matches) => Results.Ok(matches.List()));
        admin.MapGet("/matches/{matchId:int}", (int matchId, MatchModeration matches) =>
            ToMatchId(matchId) is { } id && matches.Get(id) is { } match ? Results.Ok(match) : Results.NotFound());
        admin.MapPost("/matches/{matchId:int}/abort", (int matchId, MatchModeration matches) =>
            ToMatchId(matchId) is not { } id ? Results.NotFound() : matches.Abort(id) switch
            {
                null => Results.NotFound(),
                true => Results.Ok(new { Aborted = true }),
                false => Results.Ok(new { Aborted = false }),
            });
        admin.MapPost("/matches/{matchId:int}/close", (int matchId, MatchModeration matches) =>
            ToMatchId(matchId) is { } id && matches.Close(id) ? Results.Ok(new { Closed = true }) : Results.NotFound());
        admin.MapPost("/matches/{matchId:int}/message", (int matchId, MatchMessageRequest request, MatchModeration matches) =>
        {
            var text = request.Text?.Trim();
            if (string.IsNullOrEmpty(text) || text.Length > 450)
                return Results.BadRequest(new { Error = "A message needs 1 to 450 characters." });

            return ToMatchId(matchId) is { } id && matches.Message(id, text) ? Results.Ok(new { Sent = true }) : Results.NotFound();
        });

        admin.MapGet("/online", async (PlayerRegistry players, IServiceProvider services, CancellationToken cancellationToken) =>
            Results.Ok(await OnlineAsync(players, services, cancellationToken).ConfigureAwait(false)));

        admin.MapGet("/stats", async (PlayerRegistry players, TomYumVersion version, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            var online = await OnlineAsync(players, services, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                Version = version.Display,
                Online = online.Count,
                OnlineStable = online.Count(p => p.Client == "stable"),
                OnlineLazer = online.Count(p => p.Client == "lazer"),
                OnlineWebsite = online.Count(p => p.Client == "website"),
                StartedAt = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero),
            });
        });
    }

    /// <summary>An online player for the admin panel (stable's mode/action numbers; lazer and website players mapped onto them).</summary>
    public sealed record OnlineEntry(int UserId, string Username, int Mode, int Action, string ActionInfo, int BeatmapId, DateTime LastActivity, string Client);

    /// <summary>Everyone online (<see cref="OnlinePlayers"/>): stable players with their in-game status, lazer players with theirs, website visitors as idle.</summary>
    public static async Task<IReadOnlyList<OnlineEntry>> OnlineAsync(PlayerRegistry players, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(services);

        var byId = players.All.ToDictionary(p => p.UserId);
        var online = await services.GetRequiredService<OnlinePlayers>().GetAsync(cancellationToken).ConfigureAwait(false);
        return online.Select(p => p.Client == OnlineClient.Stable && byId.TryGetValue(p.UserId, out var session)
                ? new OnlineEntry(p.UserId, p.Username, session.Presence.Mode, session.Presence.Action, session.Presence.ActionInfo,
                    session.Presence.BeatmapId, p.LastSeen, "stable")
                : new OnlineEntry(p.UserId, p.Username, p.Mode ?? 0, p.Playing ? 2 : 0,
                    (p.Client == OnlineClient.Lazer ? "on lazer" : "on the website") + (p.Doing is { } doing ? $": {doing}" : string.Empty),
                    p.BeatmapId ?? 0, p.LastSeen, p.Client == OnlineClient.Lazer ? "lazer" : "website"))
            .ToList();
    }

    /// <summary>A room id from the URL, or <c>null</c> when it can't be one (rooms are numbered 1–65535).</summary>
    private static ushort? ToMatchId(int id) => id is > 0 and <= ushort.MaxValue ? (ushort)id : null;

    /// <summary>The acting staff member's osu-web user id (0 when the request didn't say).</summary>
    public static int ActorId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return int.TryParse(context.Items[AdminAuthFilter.ActorItemKey] as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : 0;
    }

    private static object MaintenanceJson(Maintenance maintenance) => new
    {
        enabled = maintenance.State.Enabled,
        locked = maintenance.IsLocked,
        message = maintenance.State.Message,
        notice = maintenance.Notice,
        startsAt = maintenance.State.StartsAt,
        since = maintenance.State.Since,
        byUserId = maintenance.State.ByUserId,
    };

    private static async Task<IResult> SetMaintenanceAsync(MaintenanceRequest request, Maintenance maintenance, HttpContext context, CancellationToken cancellationToken)
    {
        if (request.Enabled)
        {
            var delay = TimeSpan.FromSeconds(Math.Clamp(request.DelaySeconds ?? 0, 0, 24 * 60 * 60));
            await maintenance.StartAsync(request.Message is { Length: > 500 } long_ ? long_[..500] : request.Message, delay, ActorId(context), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await maintenance.StopAsync(ActorId(context)).ConfigureAwait(false);
        }
        return Results.Ok(MaintenanceJson(maintenance));
    }

    private static async Task<IResult> SendAlertAsync(AlertRequest request, Maintenance maintenance, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return Results.BadRequest(new { error = "Write a message." });
        var text = request.Message.Trim();
        var reached = await maintenance.AlertAsync(text.Length > 500 ? text[..500] : text, request.Lazer ?? true, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { reached });
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
        HttpContext context, IHostApplicationLifetime lifetime)
    {
        // Runs to the end even when the request gives up (see ImportSetAsync).
        var cancellationToken = lifetime.ApplicationStopping;
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

        var result = await importer.ImportAsync(set, ActorId(context), cancellationToken: cancellationToken, via: Via(context)).ConfigureAwait(false);
        return result.Found ? Results.Ok(result) : Results.NotFound(result);
    }

    /// <summary>Asks the host's pp updater to update a pp library (then a pp rework follows); 400 with why when it can't.</summary>
    private static async Task<IResult> UpdatePpLibraryAsync(PpLibraryUpdateRequest request, PpLibraries libraries, IStaffLogRepository log, TimeProvider time,
        HttpContext context, CancellationToken cancellationToken)
    {
        if (await libraries.UpdateAsync(request.Library, ActorId(context)).ConfigureAwait(false) is { } error)
            return Results.BadRequest(new { error });

        await log.AddAsync(new TomYum.Domain.Entities.StaffLogEntry
        {
            CreatedAt = time.GetUtcNow().UtcDateTime,
            ActorId = ActorId(context),
            Action = "pp.library.update",
            Summary = $"Asked to update the {request.Library} pp library (a pp rework follows)",
        }, cancellationToken).ConfigureAwait(false);
        return Results.Ok();
    }

    /// <summary>Starts a pp rework; 400 with why when it can't.</summary>
    private static async Task<IResult> StartPpReworkAsync(PpReworkRequest request, PpReworks reworks, HttpContext context, CancellationToken cancellationToken)
    {
        var (rework, error) = await reworks.StartAsync(request, ActorId(context), cancellationToken).ConfigureAwait(false);
        return rework is null ? Results.BadRequest(new { error }) : Results.Ok(new { rework.Id });
    }

    /// <summary>Recalculates the pp of every passed score, map by map, and logs it.</summary>
    private static async Task<IResult> RecalculateAllScoresAsync(
        TomYum.Persistence.TomYumDbContext db, ScoreRecalculation recalculation, IStaffLogRepository log, TimeProvider time,
        HttpContext context, CancellationToken cancellationToken)
    {
        var md5s = await db.Scores.AsNoTracking().Where(s => s.Passed).Select(s => s.BeatmapMd5).Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var beatmaps = await db.Beatmaps.AsNoTracking().Where(b => md5s.Contains(b.Md5))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var total = new ScoreRecalculationResult(0, 0, 0);
        foreach (var chunk in beatmaps.Chunk(50))
        {
            var result = await recalculation.RecalculateAsync(chunk, cancellationToken).ConfigureAwait(false);
            total = new ScoreRecalculationResult(total.Scores + result.Scores, total.Changed + result.Changed, total.Players + result.Players);
        }

        await log.AddAsync(new TomYum.Domain.Entities.StaffLogEntry
        {
            CreatedAt = time.GetUtcNow().UtcDateTime,
            ActorId = ActorId(context),
            Action = "scores.recalculate",
            Summary = string.Create(CultureInfo.InvariantCulture,
                $"Recalculated pp on {beatmaps.Count} map(s): {total.Changed} of {total.Scores} score(s) changed"),
        }, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { Maps = beatmaps.Count, total.Scores, total.Changed });
    }

    /// <summary>Body of <c>POST /_admin/scores/restore</c>: the removed score's fields (from a backup) plus
    /// an optional base64-encoded <c>.osr</c> replay. The staff member who hits the panel's restore
    /// button pastes the row's values; the website turns them into this.</summary>
    public sealed record RestoreScoreRequest(ScoreRestorePayload Payload, string? ReplayBase64, string? Reason);

    /// <summary>
    /// Restores a removed score from a backup payload. Idempotent at the score-id level: if a row with
    /// the same id already exists, returns 409 Conflict and doesn't touch it. The replay is restored
    /// if provided; stats and ranks are recomputed; the website copy is written. Logged as
    /// <c>score.restore</c>.
    /// </summary>
    private static async Task<IResult> RestoreScoreAsync(RestoreScoreRequest request, ScoreModeration scores, HttpContext context, CancellationToken cancellationToken)
    {
        if (request?.Payload is null)
            return Results.BadRequest("A score payload is required.");
        byte[]? replay = null;
        if (!string.IsNullOrEmpty(request.ReplayBase64))
        {
            try
            {
                replay = Convert.FromBase64String(request.ReplayBase64);
            }
            catch (FormatException)
            {
                return Results.BadRequest("replayBase64 isn't valid base64.");
            }
        }
        return await scores.RestoreAsync(request.Payload, replay, ActorId(context), request.Reason, cancellationToken).ConfigureAwait(false) is { } result
            ? Results.Ok(result)
            : Results.Conflict(new { message = "That score id already exists (restores don't overwrite)." });
    }

    private static async Task<IResult> ImportSetAsync(int setId, BeatmapSetImporter importer, HttpContext context, IHostApplicationLifetime lifetime)
    {
        // An import runs to the end even when the request gives up (lazer's lookups do after ten seconds), so the
        // next lookup finds the maps instead of starting over.
        var cancellationToken = lifetime.ApplicationStopping;
        var result = await importer.ImportAsync(setId, ActorId(context), cancellationToken: cancellationToken, via: Via(context)).ConfigureAwait(false);
        return result.Found ? Results.Ok(result) : Results.NotFound(result);
    }

    /// <summary>The client an automatic import is for (<c>?via=</c>), when it's one we know.</summary>
    internal static string? Via(HttpContext context)
    {
        var via = context.Request.Query["via"].ToString();
        return via is "lazer" or "website" or "multiplayer" ? via : null;
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

/// <summary>POST /maintenance: on (with a message and a countdown in seconds) or off.</summary>
public sealed record MaintenanceRequest(bool Enabled, string? Message, int? DelaySeconds);

/// <summary>POST /alert: an in-game alert (and, unless <c>Lazer</c> is false, a BanchoBot message to lazer players).</summary>
public sealed record AlertRequest(string? Message, bool? Lazer);
