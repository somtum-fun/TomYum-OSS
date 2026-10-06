using System.Globalization;
using System.Net;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Performance;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Admin;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using TomYum.Server.Discord;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Anticheat;

/// <summary>
/// Sends every eligible pass (stable and lazer alike, from the website's scores) to circleguard,
/// the way production did after each submission:
/// <list type="bullet">
/// <item>eligible: passed, on a ranked, approved or loved map, combo of 5 or more, not Autopilot,
/// relax only in osu!, by a player who isn't restricted;</item>
/// <item>compared with the map's top plays here, and on osu! (official maps);</item>
/// <item>a risk above <see cref="CircleguardOptions.RestrictAbove"/> restricts the player at once
/// (staff are only reported), and at <see cref="CircleguardOptions.ReportAt"/> or more staff are
/// told on Discord with the reasons.</item>
/// </list>
/// Stable replays are TomYum's own files; lazer's are the spectator server's (read-only mount).
/// Replays can arrive a little after the score, so a score waits up to three minutes for its replay.
/// A stable pass always comes with its replay, so one that can't be checked (its replay never turns up,
/// circleguard can't read it, or it's too short to hold the play) is reported to staff; lazer's replay
/// depends on the spectator server, so a missing one is only logged.
/// </summary>
public sealed class CircleguardWatch : BackgroundService
{
    /// <summary>What the player is told (no details, like production).</summary>
    public const string PlayerReason = "[circleguard] Suspicious activity detected on your recent play.";

    /// <summary>Why a stable pass without a replay is reported.</summary>
    public const string NoReplayReason = "no replay came with this stable pass, so circleguard couldn't check it";

    /// <summary>Why a stable pass whose replay circleguard turned down is reported.</summary>
    public const string DamagedReplayReason = "circleguard couldn't read the replay that came with this stable pass";

    /// <summary>Why a stable pass with a replay of only a few frames is reported.</summary>
    public const string ShortReplayReason = "the replay that came with this stable pass is too short to hold the play";

    /// <summary>circleguard's note for a replay of under 20 frames (its Analyzer), which it doesn't judge.</summary>
    public const string TooShortNote = "The replay is too short to look at.";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReplayWait = TimeSpan.FromMinutes(3);

    /// <summary>A play the checks keep failing on (not its replay's fault) is dropped after this, so it can't hold up the rest.</summary>
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(1);

    /// <summary>How many top plays (here, and on osu!) each new pass is compared with.</summary>
    public const int References = 5;

    private readonly CircleguardOptions _options;
    private readonly CircleguardScanner _scanner;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<CircleguardWatch> _logger;
    private readonly DiscordWatch? _discord;
    private readonly List<(ScoreCard Score, DateTimeOffset SeenAt)> _waiting = [];
    private ulong _mark;
    private bool _started;

    public CircleguardWatch(
        CircleguardOptions options, CircleguardScanner scanner, IServiceScopeFactory scopes,
        TimeProvider time, ILogger<CircleguardWatch> logger, DiscordWatch? discord = null)
    {
        _options = options;
        _scanner = scanner;
        _scopes = scopes;
        _time = time;
        _logger = logger;
        _discord = discord;
    }

    /// <summary>Whether a play is looked at (see the class remarks).</summary>
    public static bool Eligible(ScoreCard score)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (!score.Passed || score.MaxCombo < 5 || score.BeatmapStatus is not (1 or 2 or 4))
            return false;
        if (score.Mods.Any(m => m is "AP" or "AT" or "CN"))
            return false;
        return !score.Mods.Contains("RX") || score.Ruleset == 0;
    }

    /// <summary>One pass: new scores join the queue; those whose replay is there are checked.</summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var community = scope.ServiceProvider.GetRequiredService<OsuWebCommunity>();
        if (!_started)
        {
            // Only plays from now on: a restart doesn't re-judge old ones.
            _mark = await community.GetLatestScoreIdAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
            return;
        }

        foreach (var score in await community.GetScoresAfterAsync(_mark, 100, cancellationToken).ConfigureAwait(false))
        {
            _mark = score.ScoreId;
            if (Eligible(score) && (score.IsStable || _options.LazerReplaysPath.Length > 0))
                _waiting.Add((score, _time.GetUtcNow()));
        }

        foreach (var entry in _waiting.ToList())
        {
            var path = await _scanner.ReplayPathAsync(community, entry.Score, cancellationToken).ConfigureAwait(false);
            if (path is null || !File.Exists(path))
            {
                if (_time.GetUtcNow() - entry.SeenAt > ReplayWait)
                {
                    _waiting.Remove(entry);
                    _logger.LogInformation("circleguard: score {ScoreId} has no replay; not checked.", entry.Score.ScoreId);
                    // (no path: the stable score is gone from the website, deleted while it waited)
                    if (entry.Score.IsStable && path is not null)
                        await ReportUncheckedAsync(scope.ServiceProvider, entry.Score, NoReplayReason, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }
            try
            {
                await CheckAsync(scope.ServiceProvider, community, entry.Score, path, cancellationToken).ConfigureAwait(false);
                _waiting.Remove(entry);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
                // circleguard turned this replay down (damaged frames): trying again won't help.
                // (a wrong key is 401: the pass fails and is tried again, like the service being down)
                _waiting.Remove(entry);
                _logger.LogWarning(ex, "circleguard: score {ScoreId}'s replay was turned down; not checked.", entry.Score.ScoreId);
                if (entry.Score.IsStable)
                    await ReportUncheckedAsync(scope.ServiceProvider, entry.Score, DamagedReplayReason, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (IOException or OperationCanceledException) && _time.GetUtcNow() - entry.SeenAt > GiveUpAfter)
            {
                // Anything else (the service down) fails the pass and is tried again, but not for ever.
                _waiting.Remove(entry);
                _logger.LogWarning(ex, "circleguard: score {ScoreId} couldn't be checked for an hour; dropped.", entry.Score.ScoreId);
            }
            catch (IOException ex)
            {
                // The spectator server is still writing the replay: next pass, within the wait.
                if (_time.GetUtcNow() - entry.SeenAt > ReplayWait)
                {
                    _waiting.Remove(entry);
                    _logger.LogWarning(ex, "circleguard: score {ScoreId}'s replay couldn't be read; not checked.", entry.Score.ScoreId);
                }
            }
        }
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
#pragma warning disable CA1031 // one failed pass (the service down, a database hiccup) must not stop the checks
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "circleguard: a pass failed; trying again shortly.");
            }
            await Task.Delay(Interval, _time, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task CheckAsync(IServiceProvider services, OsuWebCommunity community, ScoreCard score, string path, CancellationToken cancellationToken)
    {
        // Whitelisted by staff (the admin panel): anti-cheat leaves them alone.
        if (services.GetService<OsuWebAccountFlags>() is { } flags && await flags.IsWhitelistedAsync(score.UserId, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("circleguard: score {ScoreId} not checked (player {UserId} is whitelisted).", score.ScoreId, score.UserId);
            return;
        }

        var scan = await _scanner.ScanAsync(services, community, score, path, References, References, cancellationToken: cancellationToken).ConfigureAwait(false);
        var verdict = scan.Verdict;
        // A real pass is hundreds of frames: a few means the replay doesn't hold the play.
        if (score.IsStable && verdict.RiskScore < _options.ReportAt && verdict.Note == TooShortNote)
        {
            await ReportUncheckedAsync(services, score, ShortReplayReason, cancellationToken).ConfigureAwait(false);
            return;
        }
        var restricted = false;
        if (verdict.RiskScore > _options.RestrictAbove)
            restricted = await RestrictAsync(services, score, verdict, cancellationToken).ConfigureAwait(false);
        if (verdict.RiskScore >= _options.ReportAt)
        {
            if (!restricted)
                await LogFlaggedAsync(services, score, verdict, cancellationToken).ConfigureAwait(false);
            if (_discord is not null)
                await _discord.CircleguardAsync(score, scan, restricted, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A stable pass circleguard couldn't check (<paramref name="reason"/>): staff are told (staff log and Discord), nobody is restricted.</summary>
    private async Task ReportUncheckedAsync(IServiceProvider services, ScoreCard score, string reason, CancellationToken cancellationToken)
    {
        if (services.GetService<OsuWebAccountFlags>() is { } flags && await flags.IsWhitelistedAsync(score.UserId, cancellationToken).ConfigureAwait(false))
            return;
        if (services.GetService<IStaffLogRepository>() is { } log)
        {
            await log.AddAsync(new StaffLogEntry
            {
                CreatedAt = _time.GetUtcNow().UtcDateTime,
                ActorId = BanchoBot.UserId,
                Action = "anticheat.flagged",
                TargetId = score.UserId,
                Summary = string.Create(CultureInfo.InvariantCulture, $"circleguard couldn't check score {score.ScoreId}: {reason}"),
                Result = "circleguard",
            }, cancellationToken).ConfigureAwait(false);
        }
        if (_discord is not null)
            await _discord.UncheckedAsync(score, reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A flagged play that didn't restrict, kept in the staff log (the admin panel shows the last one).</summary>
    private async Task LogFlaggedAsync(IServiceProvider services, ScoreCard score, CircleguardVerdict verdict, CancellationToken cancellationToken)
    {
        if (services.GetService<IStaffLogRepository>() is not { } log)
            return;
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"circleguard flagged score {score.ScoreId} (risk {verdict.RiskScore}/100): {string.Join("; ", verdict.Reasons)}");
        await log.AddAsync(new StaffLogEntry
        {
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            ActorId = BanchoBot.UserId,
            Action = "anticheat.flagged",
            TargetId = score.UserId,
            Summary = summary.Length > 512 ? summary[..512] : summary,
            Result = "circleguard",
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Restricts the player as BanchoBot, like the GMT panel would (not staff: they're only reported).</summary>
    private async Task<bool> RestrictAsync(IServiceProvider services, ScoreCard score, CircleguardVerdict verdict, CancellationToken cancellationToken)
    {
        var user = await services.GetRequiredService<IUserRepository>().GetByIdAsync(score.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null || ModerationCommands.IsStaff(user.Privileges) || services.GetService<OsuWebModeration>() is not { } moderation)
            return false;
        if (await moderation.RestrictAsync(user.Id, BanchoBot.UserId, PlayerReason, cancellationToken).ConfigureAwait(false) != ModerationOutcome.Done)
            return false;

        var summary = string.Create(CultureInfo.InvariantCulture,
            $"circleguard restricted {user.Username} (risk {verdict.RiskScore}/100 on score {score.ScoreId}): {string.Join("; ", verdict.Reasons)}");
        await services.GetRequiredService<IStaffLogRepository>().AddAsync(new StaffLogEntry
        {
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            ActorId = BanchoBot.UserId,
            Action = "user.restrict",
            TargetId = user.Id,
            Summary = summary.Length > 512 ? summary[..512] : summary,
            Result = "circleguard",
        }, cancellationToken).ConfigureAwait(false);
        await services.GetRequiredService<UserModerationSync>().RefreshAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (services.GetService<OsuWebSync>() is { } sync)
            await sync.RecalculateCountriesOfAsync([user.Id], cancellationToken).ConfigureAwait(false);
        return true;
    }
}
