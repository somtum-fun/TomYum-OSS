using TomYum.Persistence.OsuWeb;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// osu!'s rule for sets nobody works on: a custom set still a work in progress or pending, not
/// updated for <see cref="After"/>, goes to the graveyard (checked daily). A new upload brings it
/// back as the mapper submits it. Graveyarded sets are re-indexed so the listing shows them there.
/// </summary>
public sealed class CustomGraveyardJob : BackgroundService
{
    public static readonly TimeSpan After = TimeSpan.FromDays(28);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<CustomGraveyardJob> _logger;

    public CustomGraveyardJob(IServiceScopeFactory scopes, TimeProvider time, ILogger<CustomGraveyardJob> logger)
    {
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    /// <summary>One pass; returns the sets moved.</summary>
    public async Task<IReadOnlyList<uint>> RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var moved = await scope.ServiceProvider.GetRequiredService<OsuWebCustomSets>()
            .GraveyardAsync((_time.GetUtcNow() - After).UtcDateTime, cancellationToken).ConfigureAwait(false);
        if (moved.Count == 0)
            return moved;
        _logger.LogInformation("Graveyard: {Count} custom set(s) untouched for four weeks moved there.", moved.Count);
        if (scope.ServiceProvider.GetService<OsuWebInterop>() is { IsConfigured: true } interop)
        {
            foreach (var set in moved)
                await interop.IndexBeatmapsetAsync((int)set, cancellationToken).ConfigureAwait(false);
        }
        return moved;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // a failed pass is tried again tomorrow
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "Graveyard: the daily pass failed.");
            }
            await Task.Delay(TimeSpan.FromDays(1), _time, stoppingToken).ConfigureAwait(false);
        }
    }
}
