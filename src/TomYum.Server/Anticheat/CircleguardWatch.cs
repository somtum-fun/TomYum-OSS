using System.Globalization;
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
/// </summary>
public sealed class CircleguardWatch : BackgroundService
{
    /// <summary>What the player is told (no details, like production).</summary>
    public const string PlayerReason = "[circleguard] Suspicious activity detected on your recent play.";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReplayWait = TimeSpan.FromMinutes(3);

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
                }
                continue;
            }
            try
            {
                await CheckAsync(scope.ServiceProvider, community, entry.Score, path, cancellationToken).ConfigureAwait(false);
                _waiting.Remove(entry);
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
        var scan = await _scanner.ScanAsync(services, community, score, path, References, References, cancellationToken: cancellationToken).ConfigureAwait(false);
        var verdict = scan.Verdict;
        var restricted = false;
        if (verdict.RiskScore > _options.RestrictAbove)
            restricted = await RestrictAsync(services, score, verdict, cancellationToken).ConfigureAwait(false);
        if (verdict.RiskScore >= _options.ReportAt && _discord is not null)
            await _discord.CircleguardAsync(score, scan, restricted, cancellationToken).ConfigureAwait(false);
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
