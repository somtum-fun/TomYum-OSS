using System.Globalization;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;
using TomYum.Server.Admin;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// The pp cap (<see cref="PpCapSettings"/>, set in the website's admin panel): follows the website's scores,
/// stable and lazer, and restricts a player whose passed play is worth more than its mode's cap, unless
/// they're whitelisted (Admin panel > account > anti-cheat whitelist). The restriction is BanchoBot's, in
/// the staff log like any other, and applies in-game at once. Lazer plays wait for ppy's processor's pp.
/// </summary>
public sealed class PpCapWatch : BackgroundService
{
    /// <summary>How long a lazer play may wait for the score processor before it's judged anyway.</summary>
    public static readonly TimeSpan ProcessingGrace = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SettingsFor = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<PpCapWatch> _logger;
    private ulong? _mark;
    private PpCapSettings? _settings;
    private DateTimeOffset _settingsAt;

    public PpCapWatch(IServiceScopeFactory scopes, ILogger<PpCapWatch> logger, TimeProvider? time = null)
    {
        _scopes = scopes;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>One pass over the scores set since the last one (the first pass only notes where to start).</summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var community = scope.ServiceProvider.GetRequiredService<OsuWebCommunity>();
        if (_mark is not { } mark)
        {
            _mark = await community.GetLatestScoreIdAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var now = _time.GetUtcNow();
        if (_settings is null || now - _settingsAt > SettingsFor)
        {
            _settings = await scope.ServiceProvider.GetRequiredService<OsuWebPpCaps>().GetAsync(cancellationToken).ConfigureAwait(false);
            _settingsAt = now;
        }

        foreach (var score in await community.GetScoresAfterAsync(mark, 200, cancellationToken).ConfigureAwait(false))
        {
            // Lazer plays get their pp from ppy's processor: wait (in order) until it's done.
            if (!score.IsStable && now.UtcDateTime - score.EndedAt < ProcessingGrace
                && !await community.IsProcessedAsync(score.ScoreId, cancellationToken).ConfigureAwait(false))
            {
                break;
            }
            _mark = score.ScoreId;
            if (_settings.Enabled && score.Passed && score.Pp is { } pp && _settings.CapFor(score.Ruleset, score.Mods.ToList()) is var cap && cap > 0 && pp > cap)
                await OverCapAsync(scope.ServiceProvider, score, pp, cap, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OverCapAsync(IServiceProvider services, ScoreCard score, double pp, double cap, CancellationToken cancellationToken)
    {
        if (score.UserId == BanchoBot.UserId
            || (services.GetService<OsuWebAccountFlags>() is { } flags && await flags.IsWhitelistedAsync(score.UserId, cancellationToken).ConfigureAwait(false)))
        {
            return;
        }

        var mode = score.Mods.Contains("RX", StringComparer.OrdinalIgnoreCase) ? "Relax"
            : score.Mods.Contains("AP", StringComparer.OrdinalIgnoreCase) ? "Autopilot"
            : Rulesets.Name(score.Ruleset);
        var reason = string.Create(CultureInfo.InvariantCulture,
            $"pp cap: {pp:0.##}pp on {score.Artist} - {score.Title} [{score.Version}] in {mode} (score {score.ScoreId}); the cap is {cap:0.##}pp");
        var outcome = await services.GetRequiredService<OsuWebModeration>().RestrictAsync(score.UserId, BanchoBot.UserId, reason, cancellationToken).ConfigureAwait(false);
        if (outcome != ModerationOutcome.Done)
            return;

        _logger.LogWarning("pp cap: restricted {Username} (id {UserId}): {Reason}.", score.Username, score.UserId, reason);
        await services.GetRequiredService<IStaffLogRepository>().AddAsync(new StaffLogEntry
        {
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            ActorId = BanchoBot.UserId,
            Action = "user.restrict",
            TargetId = score.UserId,
            Summary = $"Restricted {score.Username} automatically: {reason}.",
        }, cancellationToken).ConfigureAwait(false);
        await services.GetRequiredService<UserModerationSync>().RefreshAsync(score.UserId, cancellationToken).ConfigureAwait(false);
        await ModerationCommands.RecalculateCountryAsync(services, score.UserId, cancellationToken).ConfigureAwait(false);
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
#pragma warning disable CA1031 // a failed pass (website down) is tried again; the scores wait
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "pp cap: a pass failed; trying again shortly.");
            }
            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }
}
