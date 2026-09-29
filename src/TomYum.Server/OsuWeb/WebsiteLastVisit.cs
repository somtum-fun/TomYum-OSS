using Microsoft.EntityFrameworkCore;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// Players in the game (and IRC) are online on the website too: the website (and lazer, which asks
/// it) only knows who's online from their last visit, so it's kept current for everyone connected
/// here, and for BanchoBot, which is always around.
/// </summary>
public sealed class WebsiteLastVisit : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly PlayerRegistry _players;
    private readonly ILogger<WebsiteLastVisit> _logger;

    public WebsiteLastVisit(IServiceScopeFactory scopes, PlayerRegistry players, ILogger<WebsiteLastVisit> logger)
    {
        _scopes = scopes;
        _players = players;
        _logger = logger;
    }

    /// <summary>Marks everyone connected, and BanchoBot, as just seen; returns how many.</summary>
    public async Task<int> TouchAsync(CancellationToken cancellationToken = default)
    {
        var ids = _players.All.Select(p => p.UserId).Append(BanchoBot.UserId).Where(id => id > 0).Distinct().ToList();
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OsuWebDbContext>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return await db.Users.Where(u => ids.Contains((int)u.UserId))
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.UserLastvisit, (uint)now), cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TouchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Marking connected players as seen on the website failed.");
            }
            await Task.Delay(Every, stoppingToken).ConfigureAwait(false);
        }
    }
}
