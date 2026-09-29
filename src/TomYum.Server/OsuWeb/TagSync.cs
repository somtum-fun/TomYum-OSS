using TomYum.Persistence.OsuWeb;
using TomYum.Server.Beatmaps;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// Keeps the website's beatmap tags the same as osu!'s (lazer's tag voting offers these): at start,
/// then daily. Needs the osu! API v2 client (<c>Osu:ClientId</c>/<c>Osu:ClientSecret</c>).
/// </summary>
public sealed class TagSync : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TagSync> _logger;

    public TagSync(IServiceScopeFactory scopes, ILogger<TagSync> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>One pass; returns the tags added or changed (null when osu! couldn't be asked).</summary>
    public async Task<int?> SyncAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var osu = scope.ServiceProvider.GetRequiredService<OsuApiV2Client>();
        if (await osu.GetTagsAsync(cancellationToken).ConfigureAwait(false) is not { Count: > 0 } tags)
            return null;
        return await scope.ServiceProvider.GetRequiredService<OsuWebTags>().UpsertAsync(tags, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await SyncAsync(stoppingToken).ConfigureAwait(false) is { } changed and > 0)
                    _logger.LogInformation("Beatmap tags: {Changed} added or changed from osu!.", changed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Beatmap tags: sync failed.");
            }
            await Task.Delay(Every, stoppingToken).ConfigureAwait(false);
        }
    }
}
