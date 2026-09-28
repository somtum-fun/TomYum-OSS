using Microsoft.EntityFrameworkCore;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// Writes the website's online statistics once a minute (<c>osu_banchostats</c>, which bancho fills
/// on osu!): players online in stable and on lazer, and multiplayer games on each. The front page's
/// "Online Users" / "Games" and its 24-hour online graph read them.
/// </summary>
public sealed class BanchoStatsJob : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly MatchRegistry _matches;
    private readonly ILogger<BanchoStatsJob> _logger;

    public BanchoStatsJob(IServiceScopeFactory scopes, MatchRegistry matches, ILogger<BanchoStatsJob> logger)
    {
        _scopes = scopes;
        _matches = matches;
        _logger = logger;
    }

    /// <summary>Records one row now; returns (stable, lazer, stable games, lazer games).</summary>
    public async Task<(int Stable, int Lazer, int Games, int LazerGames)> RecordAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var online = await scope.ServiceProvider.GetRequiredService<OnlinePlayers>().GetAsync(cancellationToken).ConfigureAwait(false);
        var stable = online.Count(p => p.Client == OnlineClient.Stable);
        var lazer = online.Count(p => p.Client == OnlineClient.Lazer);
        var games = _matches.All.Count;

        var db = scope.ServiceProvider.GetRequiredService<OsuWebDbContext>();
        // Lazer's real-time rooms still open (playlists aren't games in progress).
        var lazerGames = (int)await db.Database.SqlQuery<long>(
            $"SELECT COUNT(*) AS Value FROM multiplayer_rooms WHERE type <> 'playlists' AND (ends_at IS NULL OR ends_at > UTC_TIMESTAMP())")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO osu_banchostats (users_irc, users_osu, users_lazer, multiplayer_games, multiplayer_games_lazer, `date`) VALUES (0, {stable}, {lazer}, {games}, {lazerGames}, UTC_TIMESTAMP())",
            cancellationToken).ConfigureAwait(false);
        return (stable, lazer, games, lazerGames);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RecordAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Recording the online statistics failed; trying again in a minute.");
            }
        }
    }
}
