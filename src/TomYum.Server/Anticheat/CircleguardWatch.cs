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

    private readonly CircleguardOptions _options;
    private readonly CircleguardClient _client;
    private readonly OfficialReplays _official;
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly ILogger<CircleguardWatch> _logger;
    private readonly DiscordWatch? _discord;
    private readonly List<(ScoreCard Score, DateTimeOffset SeenAt)> _waiting = [];
    private ulong _mark;
    private bool _started;

    public CircleguardWatch(
        CircleguardOptions options, CircleguardClient client, OfficialReplays official, IServiceScopeFactory scopes,
        IConfiguration configuration, TimeProvider time, ILogger<CircleguardWatch> logger, DiscordWatch? discord = null)
    {
        _options = options;
        _client = client;
        _official = official;
        _scopes = scopes;
        _configuration = configuration;
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
            var path = await ReplayPathAsync(community, entry.Score, cancellationToken).ConfigureAwait(false);
            if (path is null || !File.Exists(path))
            {
                if (_time.GetUtcNow() - entry.SeenAt > ReplayWait)
                {
                    _waiting.Remove(entry);
                    _logger.LogInformation("circleguard: score {ScoreId} has no replay; not checked.", entry.Score.ScoreId);
                }
                continue;
            }
            _waiting.Remove(entry);
            await CheckAsync(scope.ServiceProvider, community, entry.Score, path, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Where a website score's replay is: TomYum's file for stable scores, the spectator's for lazer.</summary>
    private async Task<string?> ReplayPathAsync(OsuWebCommunity community, ScoreCard score, CancellationToken cancellationToken)
    {
        if (!score.IsStable)
            return _options.LazerReplaysPath.Length == 0 ? null : Path.Combine(_options.LazerReplaysPath, score.ScoreId.ToString(CultureInfo.InvariantCulture));
        return await community.GetStableScoreAsync(score.ScoreId, cancellationToken).ConfigureAwait(false) is { } stable
            ? ReplayFiles.PathFor(_configuration, stable.StableScoreId)
            : null;
    }

    private async Task CheckAsync(IServiceProvider services, OsuWebCommunity community, ScoreCard score, string path, CancellationToken cancellationToken)
    {
        var replay = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        byte[]? beatmap = null;
        try
        {
            beatmap = await services.GetRequiredService<OsuFileCache>().GetAsync(score.BeatmapId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _logger.LogInformation("circleguard: no .osu for beatmap {BeatmapId}; judging the replay alone.", score.BeatmapId);
        }

        var references = new List<CircleguardReference>();
        if (score.Ruleset == 0)
        {
            // The map's best here (other players), then osu!'s own top plays for official maps.
            foreach (var top in await community.GetBeatmapLeaderboardAsync(score.BeatmapId, score.Ruleset, 8, cancellationToken).ConfigureAwait(false))
            {
                if (references.Count >= 5 || top.UserId == score.UserId || top.ScoreId == score.ScoreId)
                    continue;
                if (await ReplayPathAsync(community, top, cancellationToken).ConfigureAwait(false) is { } topPath && File.Exists(topPath))
                    references.Add(new CircleguardReference((long)top.ScoreId, top.Username, 0, await File.ReadAllBytesAsync(topPath, cancellationToken).ConfigureAwait(false), FramesOnly: false));
            }
            if (_options.CompareOfficial && score.BeatmapId < Beatmap.CustomIdFloor)
                references.AddRange(await _official.TopAsync(score.BeatmapId, score.Ruleset, cancellationToken).ConfigureAwait(false));
        }

        var verdict = await _client.AnalyzeAsync(replay, beatmap, references, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("circleguard: {Player}'s score {ScoreId} ({Mods}): risk {Risk}/100 {Reasons}",
            score.Username, score.ScoreId, verdict.Mods, verdict.RiskScore, string.Join("; ", verdict.Reasons));

        var restricted = false;
        if (verdict.RiskScore > _options.RestrictAbove)
            restricted = await RestrictAsync(services, score, verdict, cancellationToken).ConfigureAwait(false);
        if (verdict.RiskScore >= _options.ReportAt && _discord is not null)
            await _discord.CircleguardAsync(score, verdict, restricted, cancellationToken).ConfigureAwait(false);
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
