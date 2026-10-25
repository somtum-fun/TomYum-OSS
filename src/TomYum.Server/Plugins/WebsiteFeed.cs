using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Plugins.Abstractions;
using TomYum.Server.Admin;

namespace TomYum.Server.Plugins;

/// <summary>
/// Raises the plugin API's feed events (<c>FeedEvents.cs</c>) from the website's tables and TomYum's staff log, every
/// few seconds: <see cref="StaffLogEvent"/>, <see cref="ScoreProcessedEvent"/>, <see cref="MedalAwardedEvent"/>,
/// <see cref="UserRegisteredEvent"/>, <see cref="BeatmapStatusChangedEvent"/> and <see cref="ReportFiledEvent"/>. Each
/// source starts at what's new after the server started, and is only read while a plugin listens for its event.
/// </summary>
internal sealed class WebsiteFeed : BackgroundService
{
    /// <summary>How often the tables are read.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>A lazer score ppy's processor hasn't finished after this long is raised anyway.</summary>
    public static readonly TimeSpan LazerProcessingWait = TimeSpan.FromMinutes(2);

    private const int Batch = 200;

    // The website lifecycle steps that change a set's status (website BNs, the automatic rank step).
    private static readonly Dictionary<string, string> StatusEvents = new(StringComparer.Ordinal)
    {
        ["qualify"] = "qualified",
        ["rank"] = "ranked",
        ["love"] = "loved",
        ["disqualify"] = "pending",
        ["remove_from_loved"] = "pending",
    };

    private readonly IServiceScopeFactory _scopes;
    private readonly EventBus _events;
    private readonly TimeProvider _time;
    private readonly ILogger<WebsiteFeed> _log;

    private long? _staffMark;
    private ulong? _scoreMark;
    private (DateTime At, HashSet<(uint User, int Achievement)> Seen)? _medalMark;
    private uint? _userMark;
    private ulong? _eventMark;
    private uint? _reportMark;

    public WebsiteFeed(IServiceScopeFactory scopes, EventBus events, ILogger<WebsiteFeed> log, TimeProvider? time = null)
    {
        _scopes = scopes;
        _events = events;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await TickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // stopping
        }
    }

    /// <summary>One pass over every source a plugin listens for.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var tomyum = scope.ServiceProvider.GetService<TomYumDbContext>();
        var website = scope.ServiceProvider.GetService<OsuWebDbContext>();

        await RunAsync("staff log", () => StaffLogAsync(tomyum, cancellationToken)).ConfigureAwait(false);
        await RunAsync("scores", () => ScoresAsync(website, cancellationToken)).ConfigureAwait(false);
        await RunAsync("medals", () => MedalsAsync(website, cancellationToken)).ConfigureAwait(false);
        await RunAsync("registrations", () => UsersAsync(website, cancellationToken)).ConfigureAwait(false);
        await RunAsync("beatmapset events", () => BeatmapsetEventsAsync(website, cancellationToken)).ConfigureAwait(false);
        await RunAsync("reports", () => ReportsAsync(website, cancellationToken)).ConfigureAwait(false);
    }

    private async Task RunAsync(string what, Func<Task> source)
    {
        try
        {
            await source().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // one table that can't be read mustn't stop the others (the next pass tries again)
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // A warning, not an error: the server-errors event mustn't fire every five seconds for an absent table.
            _log.LogWarning(ex, "website feed: reading {What} failed", what);
        }
    }

    private async Task StaffLogAsync(TomYumDbContext? db, CancellationToken cancellationToken)
    {
        var wanted = _events.HasSubscribers<StaffLogEvent>() || _events.HasSubscribers<BeatmapStatusChangedEvent>();
        if (db is null || !wanted)
        {
            _staffMark = null;
            return;
        }

        if (_staffMark is not { } mark)
        {
            _staffMark = await db.Database.SqlQueryRaw<long?>("SELECT MAX(Id) AS Value FROM staff_log")
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0;
            return;
        }

        var rows = await db.Database.SqlQueryRaw<StaffRow>(
                "SELECT Id, Action, CAST(ActorId AS SIGNED) AS ActorId, TargetId, Summary, Result, CreatedAt FROM staff_log WHERE Id > {0} ORDER BY Id LIMIT {1}", mark, Batch)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            _staffMark = row.Id;
            var at = Utc(row.CreatedAt);
            await _events.PublishAsync(new StaffLogEvent(row.Id, row.Action, row.ActorId, row.TargetId, row.Summary, row.Result, at), cancellationToken).ConfigureAwait(false);
            if (row.Action == BeatmapStatusChanger.LogAction && row.TargetId is { } setId && !string.IsNullOrEmpty(row.Result))
                await _events.PublishAsync(new BeatmapStatusChangedEvent(setId, row.Result, row.ActorId, Automatic: false, at), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ScoresAsync(OsuWebDbContext? db, CancellationToken cancellationToken)
    {
        if (db is null || !_events.HasSubscribers<ScoreProcessedEvent>())
        {
            _scoreMark = null;
            return;
        }

        if (_scoreMark is not { } mark)
        {
            _scoreMark = await db.Database.SqlQueryRaw<ulong?>("SELECT MAX(id) AS Value FROM scores")
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0;
            return;
        }

        var rows = await db.Database.SqlQueryRaw<ScoreRow>(
                """
                SELECT CAST(s.id AS SIGNED) AS Id, CAST(s.user_id AS SIGNED) AS UserId, CAST(s.beatmap_id AS SIGNED) AS BeatmapId,
                  CAST(s.ruleset_id AS SIGNED) AS RulesetId, CAST(s.pp AS DOUBLE) AS Pp, s.`rank` AS `Rank`,
                  CAST(s.accuracy AS DOUBLE) AS Accuracy, CAST(s.total_score AS SIGNED) AS TotalScore,
                  CAST(s.max_combo AS SIGNED) AS MaxCombo, CAST(s.passed <> 0 AS SIGNED) AS Passed, CAST(s.ranked <> 0 AS SIGNED) AS Ranked,
                  CAST(s.legacy_score_id IS NULL AS SIGNED) AS IsLazer, s.ended_at AS EndedAt,
                  CAST(EXISTS (SELECT 1 FROM score_process_history h WHERE h.score_id = s.id) AS SIGNED) AS Processed
                FROM scores s WHERE s.id > {0} ORDER BY s.id LIMIT {1}
                """, mark, Batch)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var row in rows)
        {
            // Lazer plays wait for ppy's processor (their pp), in order: a later score waits behind an earlier one.
            if (row.IsLazer != 0 && row.Processed == 0 && now - row.EndedAt < LazerProcessingWait)
                break;
            _scoreMark = (ulong)row.Id;
            await _events.PublishAsync(new ScoreProcessedEvent(
                row.Id, row.UserId, row.BeatmapId, (int)row.RulesetId, row.Pp, row.Rank, row.Accuracy, row.TotalScore,
                (int)row.MaxCombo, row.Passed != 0, row.Ranked != 0, row.IsLazer != 0, Utc(row.EndedAt)), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MedalsAsync(OsuWebDbContext? db, CancellationToken cancellationToken)
    {
        if (db is null || !_events.HasSubscribers<MedalAwardedEvent>())
        {
            _medalMark = null;
            return;
        }

        if (_medalMark is not { } mark)
        {
            var newest = await db.Database.SqlQueryRaw<DateTime?>("SELECT MAX(date) AS Value FROM osu_user_achievements")
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? DateTime.UnixEpoch;
            var atNewest = await db.Database.SqlQueryRaw<MedalKey>(
                    "SELECT CAST(user_id AS SIGNED) AS UserId, CAST(achievement_id AS SIGNED) AS AchievementId FROM osu_user_achievements WHERE date = {0}", newest)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            _medalMark = (newest, atNewest.Select(k => ((uint)k.UserId, (int)k.AchievementId)).ToHashSet());
            return;
        }

        // Awards have no id: everything from the newest second seen on, less what was already raised in it.
        var rows = await db.Database.SqlQueryRaw<MedalRow>(
                """
                SELECT CAST(ua.user_id AS SIGNED) AS UserId, CAST(ua.achievement_id AS SIGNED) AS AchievementId, COALESCE(a.name, '') AS Name, ua.date AS AwardedAt
                FROM osu_user_achievements ua LEFT JOIN osu_achievements a ON a.achievement_id = ua.achievement_id
                WHERE ua.date >= {0} ORDER BY ua.date, ua.user_id, ua.achievement_id LIMIT {1}
                """, mark.At, Batch)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var (at, seen) = mark;
        foreach (var row in rows)
        {
            var key = ((uint)row.UserId, (int)row.AchievementId);
            if (row.AwardedAt == at && seen.Contains(key))
                continue;
            if (row.AwardedAt > at)
            {
                at = row.AwardedAt;
                seen = [];
            }

            seen.Add(key);
            _medalMark = (at, seen);
            await _events.PublishAsync(new MedalAwardedEvent(row.UserId, row.AchievementId, row.Name, Utc(row.AwardedAt)), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UsersAsync(OsuWebDbContext? db, CancellationToken cancellationToken)
    {
        if (db is null || !_events.HasSubscribers<UserRegisteredEvent>())
        {
            _userMark = null;
            return;
        }

        if (_userMark is not { } mark)
        {
            _userMark = await db.Database.SqlQueryRaw<uint?>("SELECT MAX(user_id) AS Value FROM phpbb_users")
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0;
            return;
        }

        var rows = await db.Database.SqlQueryRaw<UserRow>(
                "SELECT CAST(user_id AS SIGNED) AS UserId, username AS Username, COALESCE(country_acronym, 'XX') AS Country, CAST(user_regdate AS SIGNED) AS RegisteredAt FROM phpbb_users WHERE user_id > {0} ORDER BY user_id LIMIT {1}",
                mark, Batch)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            _userMark = (uint)row.UserId;
            await _events.PublishAsync(new UserRegisteredEvent(row.UserId, row.Username, row.Country.ToLowerInvariant(), DateTimeOffset.FromUnixTimeSeconds(row.RegisteredAt)), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task BeatmapsetEventsAsync(OsuWebDbContext? db, CancellationToken cancellationToken)
    {
        if (db is null || !_events.HasSubscribers<BeatmapStatusChangedEvent>())
        {
            _eventMark = null;
            return;
        }

        if (_eventMark is not { } mark)
        {
            _eventMark = await db.Database.SqlQueryRaw<ulong?>("SELECT MAX(id) AS Value FROM beatmapset_events")
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0;
            return;
        }

        var rows = await db.Database.SqlQueryRaw<SetEventRow>(
                "SELECT CAST(id AS SIGNED) AS Id, CAST(beatmapset_id AS SIGNED) AS BeatmapsetId, CAST(user_id AS SIGNED) AS UserId, CAST(type AS CHAR) AS Type, created_at AS CreatedAt FROM beatmapset_events WHERE id > {0} ORDER BY id LIMIT {1}",
                mark, Batch)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            _eventMark = (ulong)row.Id;
            if (row.Type is { } type && StatusEvents.TryGetValue(type, out var status))
            {
                await _events.PublishAsync(new BeatmapStatusChangedEvent(
                    row.BeatmapsetId, status, row.UserId ?? 0, Automatic: row.UserId is null, Utc(row.CreatedAt ?? _time.GetUtcNow().UtcDateTime)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ReportsAsync(OsuWebDbContext? db, CancellationToken cancellationToken)
    {
        if (db is null || !_events.HasSubscribers<ReportFiledEvent>())
        {
            _reportMark = null;
            return;
        }

        if (_reportMark is not { } mark)
        {
            _reportMark = await db.Database.SqlQueryRaw<uint?>("SELECT MAX(report_id) AS Value FROM osu_user_reports")
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0;
            return;
        }

        var rows = await db.Database.SqlQueryRaw<ReportRow>(
                "SELECT CAST(report_id AS SIGNED) AS ReportId, CAST(reporter_id AS SIGNED) AS ReporterId, CAST(user_id AS SIGNED) AS UserId, CAST(reason AS CHAR) AS Reason, COALESCE(comments, '') AS Comments, timestamp AS ReportedAt FROM osu_user_reports WHERE report_id > {0} ORDER BY report_id LIMIT {1}",
                mark, Batch)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            _reportMark = (uint)row.ReportId;
            await _events.PublishAsync(new ReportFiledEvent(row.ReportId, row.ReporterId, row.UserId, row.Reason ?? string.Empty, row.Comments, Utc(row.ReportedAt)), cancellationToken).ConfigureAwait(false);
        }
    }

    private static DateTimeOffset Utc(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc));

    private sealed record StaffRow(long Id, string Action, long ActorId, long? TargetId, string Summary, string? Result, DateTime CreatedAt);

    private sealed record ScoreRow(long Id, long UserId, long BeatmapId, long RulesetId, double? Pp, string Rank, double Accuracy, long TotalScore, long MaxCombo, long Passed, long Ranked, long IsLazer, DateTime EndedAt, long Processed);

    private sealed record MedalKey(long UserId, long AchievementId);

    private sealed record MedalRow(long UserId, long AchievementId, string Name, DateTime AwardedAt);

    private sealed record UserRow(long UserId, string Username, string Country, long RegisteredAt);

    private sealed record SetEventRow(long Id, long BeatmapsetId, long? UserId, string? Type, DateTime? CreatedAt);

    private sealed record ReportRow(long ReportId, long ReporterId, long UserId, string? Reason, string Comments, DateTime ReportedAt);
}
