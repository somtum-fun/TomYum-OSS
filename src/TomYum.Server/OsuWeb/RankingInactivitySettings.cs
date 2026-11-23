using Microsoft.EntityFrameworkCore;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// The rankings' inactivity rule as staff set it in the website's admin panel (tomyum_site_settings
/// <c>ranking_inactive_days</c> and <c>ranking_inactive_mode</c>), read every minute into
/// <see cref="RankingInactivity.Days"/>. Mode <c>remove</c> (the default) takes inactive players off the rankings;
/// <c>grey</c> keeps them ranked (the website only greys them out), so here the rule is off. Without a stored days
/// value the startup setting (<c>Ranking:InactiveDays</c>) stands. When the rule changes, the website's stored global
/// ranks are worked out again at once, so the website and the game agree.
/// </summary>
public sealed class RankingInactivitySettings : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RankingInactivitySettings> _logger;
    private readonly int _configuredDays;

    public RankingInactivitySettings(IServiceScopeFactory scopes, ILogger<RankingInactivitySettings> logger)
    {
        _scopes = scopes;
        _logger = logger;
        _configuredDays = RankingInactivity.Days;
    }

    /// <summary>The days the rule takes players off after (0: off), from the two settings and the startup value.</summary>
    public static int Effective(string? days, string? mode, int configuredDays)
    {
        if (string.Equals(mode?.Trim(), "grey", StringComparison.OrdinalIgnoreCase))
            return 0;

        return int.TryParse(days?.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int stored) && stored >= 0
            ? stored
            : configuredDays;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OsuWebDbContext>();
        var rows = await db.Database.SqlQueryRaw<SettingRow>(
                "SELECT name AS Name, value AS Value FROM tomyum_site_settings WHERE name IN ('ranking_inactive_days', 'ranking_inactive_mode')")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var byName = rows.ToDictionary(r => r.Name, r => r.Value, StringComparer.Ordinal);

        int days = Effective(byName.GetValueOrDefault("ranking_inactive_days"), byName.GetValueOrDefault("ranking_inactive_mode"), _configuredDays);
        if (days != RankingInactivity.Days)
        {
            _logger.LogInformation("Ranking inactivity: {Before} → {After} days (0: off).", RankingInactivity.Days, days);
            RankingInactivity.Days = days;

            // The website's ranks are stored (rank_score_index): work them out again under the new rule, once.
            if (scope.ServiceProvider.GetService<OsuWebScoreMirror>() is { } mirror)
            {
                for (var mode = 0; mode < 4; mode++)
                    await mirror.RecomputeGlobalRanksAsync(mode, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The last value stays.
                _logger.LogWarning(ex, "Couldn't read the ranking inactivity settings.");
            }

            try
            {
                await Task.Delay(Every, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private sealed record SettingRow(string Name, string? Value);
}
