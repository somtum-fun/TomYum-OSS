using System.Text.Json;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Performance;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// Relax on lazer: ppy's score processor doesn't rank Relax, so lazer's RX plays (osu!, taiko,
/// catch) get nothing on the website. This takes them into TomYum's relax system, the same as a
/// stable relax play: relax pp, the relax leaderboard (a new best when it's worth more pp), and the
/// player's relax stats (plays, scores, weighted pp and accuracy), with their in-game panel
/// refreshed. It follows the website's scores and remembers how far it got (<see cref="OsuWebLazerRelax.MarkName"/>),
/// so nothing is taken twice; the first start begins at the newest score.
/// </summary>
public sealed class LazerRelaxImport : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly PlayerRegistry _players;
    private readonly ILogger<LazerRelaxImport> _logger;

    public LazerRelaxImport(IServiceScopeFactory scopes, PlayerRegistry players, ILogger<LazerRelaxImport> logger)
    {
        _scopes = scopes;
        _players = players;
        _logger = logger;
    }

    /// <summary>One pass; returns the plays taken in.</summary>
    public async Task<int> TickAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        var website = services.GetRequiredService<OsuWebLazerRelax>();
        if (await website.MarkAsync(cancellationToken).ConfigureAwait(false) is not { } mark)
        {
            await website.SetMarkAsync(await website.NewestScoreIdAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var taken = 0;
        foreach (var play in await website.AfterAsync(mark, 100, cancellationToken).ConfigureAwait(false))
        {
            if (await TakeAsync(services, play, cancellationToken).ConfigureAwait(false))
                taken++;
            await website.SetMarkAsync(play.ScoreId, cancellationToken).ConfigureAwait(false);
        }
        return taken;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // a failed pass (database hiccup) is tried again; the plays wait
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "Lazer relax: a pass failed; trying again shortly.");
            }
            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>The stable counts for a lazer play's statistics (as osu!'s own conversion counts them).</summary>
    public static (int N300, int N100, int N50, int NMiss, int NGeki, int NKatu) Counts(int ruleset, string data)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(data) ? "{}" : data);
        int Stat(string name) => document.RootElement.TryGetProperty("statistics", out var stats)
                                 && stats.TryGetProperty(name, out var value) && value.TryGetInt32(out var count) ? count : 0;
        return ruleset switch
        {
            // catch: fruits, drops, droplets; missed droplets are katu, missed drops are misses.
            2 => (Stat("great"), Stat("large_tick_hit"), Stat("small_tick_hit"), Stat("miss") + Stat("large_tick_miss"), 0, Stat("small_tick_miss")),
            _ => (Stat("great"), Stat("ok"), Stat("meh"), Stat("miss"), 0, 0),
        };
    }

    /// <summary>The play's mods as the stable bitmask (the ones stable has; Classic and lazer-only ones are left out).</summary>
    public static Mods LegacyMods(string data)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(data) ? "{}" : data);
        var mods = Mods.None;
        if (document.RootElement.TryGetProperty("mods", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var mod in list.EnumerateArray())
            {
                if (mod.TryGetProperty("acronym", out var acronym) && NowPlaying.ParseAcronyms(acronym.GetString()) is { } known)
                    mods |= known;
            }
        }
        return mods;
    }

    private async Task<bool> TakeAsync(IServiceProvider services, LazerRelaxPlay play, CancellationToken cancellationToken)
    {
        var mods = LegacyMods(play.Data);
        if (!mods.HasFlag(Mods.Relax))
            return false;
        var gameMode = mods.ToGameMode(play.Ruleset);

        var beatmaps = services.GetRequiredService<IBeatmapRepository>();
        var beatmap = await beatmaps.GetByIdAsync((int)play.BeatmapId, cancellationToken).ConfigureAwait(false);
        if (beatmap is null)
        {
            _logger.LogInformation("Lazer relax: beatmap {BeatmapId} isn't known here; score {ScoreId} skipped.", play.BeatmapId, play.ScoreId);
            return false;
        }

        var (n300, n100, n50, nmiss, ngeki, nkatu) = Counts(play.Ruleset, play.Data);
        var scores = services.GetRequiredService<IScoreRepository>();
        var ppValue = 0.0;
        if (play.Passed && beatmap.Status.AwardsRankedPp())
        {
            try
            {
                ppValue = await services.GetRequiredService<IPerformanceCalculator>().CalculateAsync(
                    new PerformanceRequest(beatmap.Id, play.Ruleset, (int)mods, (int)play.MaxCombo, n300, n100, n50, nmiss, ngeki, nkatu, Passed: true),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Lazer relax: pp for score {ScoreId} couldn't be worked out; recorded as 0.", play.ScoreId);
            }
        }

        var previous = await scores.GetPersonalBestAsync((int)play.UserId, beatmap.Md5, gameMode, cancellationToken).ConfigureAwait(false);
        var status = play.Passed
            ? (previous is null || ppValue > previous.Pp ? ScoreStatus.Best : ScoreStatus.Submitted)
            : ScoreStatus.Failed;
        if (status == ScoreStatus.Best && previous is not null)
            await scores.MarkPreviousBestAsync((int)play.UserId, beatmap.Md5, gameMode, cancellationToken).ConfigureAwait(false);

        var value = play.LegacyTotalScore > 0 ? play.LegacyTotalScore : play.TotalScore;
        await scores.AddAsync(new Score
        {
            UserId = (int)play.UserId,
            BeatmapId = beatmap.Id,
            BeatmapMd5 = beatmap.Md5,
            Mode = gameMode,
            ScoreValue = value,
            MaxCombo = (int)play.MaxCombo,
            Count300 = n300,
            Count100 = n100,
            Count50 = n50,
            CountMiss = nmiss,
            CountGeki = ngeki,
            CountKatu = nkatu,
            Grade = play.Passed ? play.Grade : "F",
            Mods = (int)mods,
            Perfect = nmiss == 0 && play.Passed,
            Passed = play.Passed,
            Pp = ppValue,
            Status = status,
            SubmittedAt = new DateTimeOffset(DateTime.SpecifyKind(play.EndedAt, DateTimeKind.Utc)),
        }, cancellationToken).ConfigureAwait(false);

        var userStats = services.GetRequiredService<IUserStatsRepository>();
        var stats = await userStats.GetOrCreateAsync((int)play.UserId, gameMode, cancellationToken).ConfigureAwait(false);
        stats.PlayCount++;
        stats.TotalScore += value;
        if (status == ScoreStatus.Best)
        {
            stats.RankedScore = Math.Max(0, stats.RankedScore + value - (previous?.ScoreValue ?? 0));
            if (PerformanceTotals.Compute(await scores.GetBestPerformancesAsync((int)play.UserId, gameMode, cancellationToken).ConfigureAwait(false)) is { } totals)
            {
                stats.Pp = totals.Pp;
                stats.Accuracy = totals.Accuracy;
            }
        }
        stats = await userStats.UpdateAsync(stats, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Lazer relax: took score {ScoreId} ({Pp:0.##}pp, {Status}) into {Mode}.", play.ScoreId, ppValue, status, GameMode.ToShortName(gameMode));

        // A stable player online in that relax mode sees it.
        if (_players.GetByUserId((int)play.UserId) is { } player && player.Presence.GameMode == gameMode)
        {
            await new PresenceStats(userStats).FillAsync(player.Presence, player.UserId, gameMode, stats, cancellationToken).ConfigureAwait(false);
            var writer = new BanchoPacketWriter(128);
            LoginResponseBuilder.WriteStats(writer, player.Presence);
            _players.EnqueueAbout(player, writer.ToArray());
        }
        return true;
    }
}
