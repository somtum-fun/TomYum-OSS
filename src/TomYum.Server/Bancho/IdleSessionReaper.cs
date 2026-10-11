namespace TomYum.Server.Bancho;

/// <summary>
/// Logs out game sessions whose client stopped polling. osu! sends its LOGOUT from a background thread
/// that often doesn't get to run when the game closes or crashes, and without this such a player stayed
/// "online" until someone noticed (and a login from another machine was refused meanwhile). A connected
/// client polls at least every 22 seconds when idle, so a minute of silence means it's gone.
/// IRC sessions and BanchoBot keep themselves; TCP sessions end with their connection.
/// </summary>
public sealed class IdleSessionReaper : BackgroundService
{
    /// <summary>A session silent for this long is logged out.</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    private readonly PlayerRegistry _players;
    private readonly IServiceProvider _services;
    private readonly ILogger<IdleSessionReaper> _logger;

    public IdleSessionReaper(PlayerRegistry players, IServiceProvider services, ILogger<IdleSessionReaper> logger)
    {
        _players = players;
        _services = services;
        _logger = logger;
    }

    /// <summary>The sessions to log out at <paramref name="now"/>.</summary>
    public static IEnumerable<OnlinePlayer> Idle(IEnumerable<OnlinePlayer> sessions, DateTimeOffset now) =>
        sessions.Where(p => !p.IsIrc && p.UserId != BanchoBot.UserId && now - p.LastActivityUtc > IdleLimit);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            foreach (var player in Idle(_players.AllSessions, DateTimeOffset.UtcNow).ToList())
            {
                _logger.LogInformation("{Username} (id {UserId}) stopped polling; logged out.", player.Presence.Username, player.UserId);
                await SessionEnd.LogOutAsync(player, _services, _logger).ConfigureAwait(false);
            }
        }
    }
}
