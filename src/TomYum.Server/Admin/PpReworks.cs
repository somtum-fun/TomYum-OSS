using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Performance;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Admin;

/// <summary>A pp rework to start: the modes (TomYum's ids), the library and versions it's for, and why.</summary>
public sealed record PpReworkRequest(IReadOnlyList<int> Modes, string? Library, string? FromVersion, string? ToVersion, string? Reason);

/// <summary>
/// pp reworks, as osu! runs them when its pp changes. Starting one pauses its modes
/// (<see cref="RankingPause"/>): rankings, leaderboards and totals stay as they were, and new scores'
/// pp waits (<see cref="Score.PpHeld"/>). Every passed score in those modes is worked out again,
/// a batch at a time, into <c>pp_rework_scores</c> (scores made meanwhile join in, so their pp comes
/// with the rest; a restart carries on from <see cref="PpRework.Cursor"/>). Then it switches over at
/// once: the new pp, each player's best per map, everyone's totals, lazer plays' pp on the website.
/// Vanilla modes then wait for the website: their scores are handed to ppy's score processor, the
/// website's ranking page shows the ranking as it was until it's through, and then ranks follow.
/// </summary>
public sealed class PpReworks : BackgroundService
{
    private const int BatchSize = 200;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PpReworks> _logger;
    private readonly TimeProvider _time;

    /// <param name="scopes">For the databases and the pp calculator.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="time">The clock.</param>
    public PpReworks(IServiceScopeFactory scopes, ILogger<PpReworks> logger, TimeProvider time)
    {
        _scopes = scopes;
        _logger = logger;
        _time = time;
    }

    /// <summary>The modes a rework can run in: vanilla, Relax (not mania) and Autopilot.</summary>
    public static readonly IReadOnlyList<int> KnownModes = [0, 1, 2, 3, 4, 5, 6, 8];

    /// <summary>Starts a rework; null with why when it can't (a mode is being reworked already, or there are none).</summary>
    public async Task<(PpRework? Rework, string? Error)> StartAsync(PpReworkRequest request, int actorId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var modes = request.Modes.Distinct().Order().ToList();
        if (modes.Count == 0 || modes.Any(m => !KnownModes.Contains(m)))
            return (null, "Pick the modes to rework.");

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TomYumDbContext>();
        var busy = (await db.PpReworks.AsNoTracking().Where(r => r.State == PpRework.Running || r.State == PpRework.Website)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).SelectMany(r => r.ModeList).ToHashSet();
        if (modes.Where(busy.Contains).Select(m => (int?)m).FirstOrDefault() is { } clash)
            return (null, $"{GameMode.ToShortName(clash)} is being reworked already.");

        var rework = new PpRework
        {
            Library = request.Library is "vanilla" or "akatsuki" ? request.Library : "manual",
            FromVersion = request.FromVersion?.Trim() ?? string.Empty,
            ToVersion = request.ToVersion?.Trim() ?? string.Empty,
            Modes = string.Join(',', modes.Select(m => m.ToString(CultureInfo.InvariantCulture))),
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            ActorId = actorId,
            StartedAt = _time.GetUtcNow().UtcDateTime,
            Total = await db.Scores.CountAsync(s => modes.Contains(s.Mode) && s.Passed, cancellationToken).ConfigureAwait(false),
        };
        db.PpReworks.Add(rework);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        RankingPause.Set(RankingPause.Modes.Concat(modes));

        // The website's ranking pages keep showing the vanilla modes as they stand.
        if (scope.ServiceProvider.GetService<OsuWebScoreMirror>() is { } mirror)
        {
            foreach (var mode in modes.Where(OsuWebScores.IsMirrored))
                await mirror.SnapshotRankingAsync(mode, cancellationToken).ConfigureAwait(false);
        }

        await LogAsync(scope, actorId, "pp.rework.start", rework.Id,
            $"pp rework #{rework.Id} started for {ModeNames(modes)} ({rework.Total} score(s)){(rework.Reason is null ? "" : ": " + rework.Reason)}", cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("pp rework #{Id} started for {Modes}: {Total} score(s).", rework.Id, rework.Modes, rework.Total);
        return (rework, null);
    }

    /// <summary>Stops a running rework: the pp stays as it was (held scores keep theirs), and its modes go back to normal.</summary>
    public async Task<bool> CancelAsync(int id, int actorId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TomYumDbContext>();
        var rework = await db.PpReworks.FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        if (rework is null || !rework.IsActive)
            return false;

        await SwitchOverAsync(scope, rework, apply: false, cancellationToken).ConfigureAwait(false);
        await FinishAsync(scope, rework, PpRework.Cancelled, cancellationToken).ConfigureAwait(false);
        await LogAsync(scope, actorId, "pp.rework.cancel", rework.Id, $"pp rework #{rework.Id} cancelled ({ModeNames(rework.ModeList)})", cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The running reworks and the latest finished ones, newest first, with the website's queue for those waiting on it.</summary>
    public async Task<object> StatusAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TomYumDbContext>();
        var reworks = await db.PpReworks.AsNoTracking().OrderByDescending(r => r.Id).Take(10).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new
        {
            Active = reworks.Where(r => r.IsActive).Select(View).ToList(),
            Recent = reworks.Where(r => !r.IsActive).Select(View).ToList(),
        };
    }

    private static object View(PpRework r) => new
    {
        r.Id, r.Library, r.FromVersion, r.ToVersion, Modes = r.ModeList, r.Reason, r.State,
        r.Total, Done = r.Completed, r.WebsiteTotal, r.WebsiteLeft, r.ActorId, r.Error, r.StartedAt, r.FinishedAt,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var busy = false;
            try
            {
                busy = await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // a failed pass is tried again; the rework waits
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "pp rework: a pass failed; trying again shortly.");
            }
            if (!busy)
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One step of the running reworks; true while there's more to do straight away.</summary>
    public async Task<bool> TickAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();

        // An update the host's pp updater put live: its rework.
        if (scope.ServiceProvider.GetService<PpLibraries>() is { } libraries
            && await libraries.TakeReworkRequestAsync().ConfigureAwait(false) is { } fromUpdater)
        {
            var (_, error) = await StartAsync(new PpReworkRequest(fromUpdater.Modes, fromUpdater.Library, fromUpdater.FromVersion, fromUpdater.ToVersion, fromUpdater.Reason),
                fromUpdater.ActorId, cancellationToken).ConfigureAwait(false);
            if (error is not null)
                _logger.LogWarning("pp rework after the {Library} update couldn't start: {Error}", fromUpdater.Library, error);
        }

        var db = scope.ServiceProvider.GetRequiredService<TomYumDbContext>();
        var active = await db.PpReworks.Where(r => r.State == PpRework.Running || r.State == PpRework.Website)
            .OrderBy(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        RankingPause.Set(active.SelectMany(r => r.ModeList));

        var busy = false;
        foreach (var rework in active)
        {
            if (rework.State == PpRework.Running)
                busy |= await RecalculateBatchAsync(scope, rework, cancellationToken).ConfigureAwait(false);
            else
                await CheckWebsiteAsync(scope, rework, cancellationToken).ConfigureAwait(false);
        }
        return busy;
    }

    /// <summary>The next scores' new pp, kept aside; the switch-over when there are none left.</summary>
    private async Task<bool> RecalculateBatchAsync(IServiceScope scope, PpRework rework, CancellationToken cancellationToken)
    {
        var db = scope.ServiceProvider.GetRequiredService<TomYumDbContext>();
        var modes = rework.ModeList.ToList();
        var batch = await db.Scores.AsNoTracking()
            .Where(s => s.Id > rework.Cursor && modes.Contains(s.Mode) && s.Passed)
            .OrderBy(s => s.Id).Take(BatchSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (batch.Count == 0)
        {
            await SwitchOverAsync(scope, rework, apply: true, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var md5s = batch.Select(s => s.BeatmapMd5).Distinct().ToList();
        var beatmaps = await db.Beatmaps.AsNoTracking().Where(b => md5s.Contains(b.Md5))
            .ToDictionaryAsync(b => b.Md5, cancellationToken).ConfigureAwait(false);
        var pp = scope.ServiceProvider.GetRequiredService<IPerformanceCalculator>();

        foreach (var score in batch)
        {
            var value = 0.0;
            if (beatmaps.TryGetValue(score.BeatmapMd5, out var beatmap) && beatmap.Status.AwardsRankedPp())
            {
                try
                {
                    value = await pp.CalculateAsync(new PerformanceRequest(
                        BeatmapId: beatmap.Id,
                        RulesetId: GameMode.AsVanilla(score.Mode),
                        Mods: score.Mods,
                        MaxCombo: score.MaxCombo,
                        Count300: score.Count300,
                        Count100: score.Count100,
                        Count50: score.Count50,
                        CountMiss: score.CountMiss,
                        CountGeki: score.CountGeki,
                        CountKatu: score.CountKatu,
                        Passed: score.Passed,
                        Lazer: score.SliderEndHits is not null, LargeTickHits: score.LargeTickHits, SliderEndHits: score.SliderEndHits), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Keep what it had rather than wiping it over a calculator hiccup.
                    _logger.LogWarning(ex, "pp rework #{Id}: score {ScoreId} couldn't be worked out; keeping {Pp}pp.", rework.Id, score.Id, score.Pp);
                    value = score.Pp;
                }
            }
            db.PpReworkScores.Add(new PpReworkScore { ReworkId = rework.Id, ScoreId = score.Id, Pp = value });
        }

        rework.Cursor = batch[^1].Id;
        rework.Completed += batch.Count;
        rework.Total = Math.Max(rework.Total, rework.Completed);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return true;
    }

    /// <summary>
    /// Everything at once: the new pp (unless <paramref name="apply"/> is false: a cancel), each
    /// player's best per map, the held scores let go, everyone's totals, and lazer plays' pp on the
    /// website. Vanilla modes then go to the website's processor; the others are done.
    /// </summary>
    private async Task SwitchOverAsync(IServiceScope scope, PpRework rework, bool apply, CancellationToken cancellationToken)
    {
        var db = scope.ServiceProvider.GetRequiredService<TomYumDbContext>();
        var modes = rework.ModeList.ToList();
        var modeList = string.Join(',', modes.Select(m => m.ToString(CultureInfo.InvariantCulture)));

        if (apply)
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE scores s JOIN pp_rework_scores r ON r.ScoreId = s.Id AND r.ReworkId = {rework.Id} SET s.Pp = r.Pp", cancellationToken)
                .ConfigureAwait(false);
        }

        // Each player's best per map, as score submission picks it (GameMode.BeatsBest): the most pp on
        // Relax/Autopilot boards (score breaks ties, and decides alone on maps without pp), the most score
        // on vanilla ones. Held scores take part like any other.
        foreach (var mode in modes)
        {
            var scores = await db.Scores.AsNoTracking().Where(s => s.Mode == mode && s.Passed)
                .Select(s => new { s.Id, s.UserId, s.BeatmapMd5, s.Pp, s.ScoreValue, s.Status })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var bests = scores.GroupBy(s => (s.UserId, s.BeatmapMd5))
                .Select(g => (GameMode.UsesPpLeaderboard(mode)
                        ? g.OrderByDescending(s => s.Pp).ThenByDescending(s => s.ScoreValue)
                        : g.OrderByDescending(s => s.ScoreValue)).ThenBy(s => s.Id).First().Id)
                .ToHashSet();
            var toBest = scores.Where(s => bests.Contains(s.Id) && s.Status != ScoreStatus.Best).Select(s => s.Id).ToList();
            var toSubmitted = scores.Where(s => !bests.Contains(s.Id) && s.Status == ScoreStatus.Best).Select(s => s.Id).ToList();
            foreach (var chunk in toBest.Chunk(1000))
                await db.Scores.Where(s => chunk.Contains(s.Id)).ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, ScoreStatus.Best), cancellationToken).ConfigureAwait(false);
            foreach (var chunk in toSubmitted.Chunk(1000))
                await db.Scores.Where(s => chunk.Contains(s.Id)).ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, ScoreStatus.Submitted), cancellationToken).ConfigureAwait(false);
        }
        await db.Scores.Where(s => modes.Contains(s.Mode) && s.PpHeld)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.PpHeld, false), cancellationToken).ConfigureAwait(false);

        // Everyone's totals in those modes.
        var stats = await db.UserStats.Where(s => modes.Contains(s.Mode)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var chunk in stats.Chunk(100))
        {
            foreach (var row in chunk)
                await BestScoreStats.ApplyAsync(db, row, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        db.ChangeTracker.Clear();
        await db.PpReworkScores.Where(s => s.ReworkId == rework.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // Lazer Relax/Autopilot plays show their pp on the website.
        if (scope.ServiceProvider.GetService<OsuWebLazerRelax>() is { } lazer)
        {
            var linked = await db.Scores.AsNoTracking()
                .Where(s => modes.Contains(s.Mode) && s.OsuWebScoreId != null)
                .Select(s => new { s.OsuWebScoreId, s.Pp, s.Passed })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var play in linked)
                await lazer.SetPpAsync(play.OsuWebScoreId!.Value, play.Passed && play.Pp > 0 ? play.Pp : null, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("pp rework #{Id}: switched over for {Modes}{Applied}.", rework.Id, modeList, apply ? "" : " (cancelled: pp kept)");

        // Vanilla modes: the website's copies are ppy's processor's to redo.
        var vanilla = modes.Where(OsuWebScores.IsMirrored).ToList();
        var performance = scope.ServiceProvider.GetService<OsuWebPerformanceSync>();
        var mirror = scope.ServiceProvider.GetService<OsuWebScoreMirror>();
        if (apply && vanilla.Count > 0 && performance is { Enabled: true } && mirror is not null)
        {
            var queued = 0;
            foreach (var mode in vanilla)
            {
                var ids = await mirror.GetScoreIdsInModeAsync(mode, cancellationToken).ConfigureAwait(false);
                foreach (var chunk in ids.Chunk(500))
                    await performance.QueueAsync(chunk, reprocess: true, cancellationToken).ConfigureAwait(false);
                queued += ids.Count;
            }
            rework.State = PpRework.Website;
            rework.WebsiteTotal = queued;
            rework.WebsiteLeft = queued;
            db.PpReworks.Update(rework);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("pp rework #{Id}: {Count} website score(s) handed to the score processor.", rework.Id, queued);
            return;
        }

        if (apply)
            await FinishAsync(scope, rework, PpRework.Done, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The website stage: done once the processor's queue is empty.</summary>
    private async Task CheckWebsiteAsync(IServiceScope scope, PpRework rework, CancellationToken cancellationToken)
    {
        var db = scope.ServiceProvider.GetRequiredService<TomYumDbContext>();
        var left = scope.ServiceProvider.GetService<TomYum.Caching.OsuWebScoreProcessing>() is { } processing
            ? (int)Math.Min(await processing.LengthAsync().ConfigureAwait(false), int.MaxValue)
            : 0;
        if (left > 0)
        {
            if (left != rework.WebsiteLeft)
            {
                rework.WebsiteLeft = left;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        rework.WebsiteLeft = 0;
        await FinishAsync(scope, rework, PpRework.Done, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ends a rework: its modes go back to normal, with ranks (and in-game pp) following the new pp.</summary>
    private async Task FinishAsync(IServiceScope scope, PpRework rework, string state, CancellationToken cancellationToken)
    {
        var db = scope.ServiceProvider.GetRequiredService<TomYumDbContext>();
        rework.State = state;
        rework.FinishedAt = _time.GetUtcNow().UtcDateTime;
        if (db.Entry(rework).State == EntityState.Detached)
            db.PpReworks.Update(rework);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var modes = rework.ModeList;
        RankingPause.Set(RankingPause.Modes.Except(modes));
        if (scope.ServiceProvider.GetService<OsuWebScoreMirror>() is { } mirror)
        {
            var performance = scope.ServiceProvider.GetService<OsuWebPerformanceSync>();
            foreach (var mode in modes.Where(OsuWebScores.IsMirrored))
            {
                await mirror.DropRankingSnapshotAsync(mode, cancellationToken).ConfigureAwait(false);
                await mirror.RecomputeGlobalRanksAsync(mode, cancellationToken).ConfigureAwait(false);
                if (performance is { Enabled: true })
                {
                    var players = await db.UserStats.AsNoTracking().Where(s => s.Mode == mode).Select(s => s.UserId)
                        .ToListAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var userId in players)
                    {
                        await performance.ApplyAsync(userId, mode, recompute: false, cancellationToken).ConfigureAwait(false);
                        await performance.RefreshPresenceAsync(userId, mode, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        if (state == PpRework.Done)
        {
            await LogAsync(scope, 0, "pp.rework.done", rework.Id, $"pp rework #{rework.Id} done for {ModeNames(modes)} ({rework.Completed} score(s))", cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("pp rework #{Id} done: {Count} score(s).", rework.Id, rework.Completed);
        }
    }

    private static string ModeNames(IEnumerable<int> modes) => string.Join(", ", modes.Select(GameMode.ToShortName));

    private async Task LogAsync(IServiceScope scope, int actorId, string action, int reworkId, string summary, CancellationToken cancellationToken)
    {
        await scope.ServiceProvider.GetRequiredService<IStaffLogRepository>().AddAsync(new StaffLogEntry
        {
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            ActorId = actorId,
            Action = action,
            TargetId = reworkId,
            Summary = summary,
        }, cancellationToken).ConfigureAwait(false);
    }
}
