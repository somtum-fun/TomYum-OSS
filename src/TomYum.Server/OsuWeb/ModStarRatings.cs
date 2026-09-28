using TomYum.Performance;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// Star ratings with mods for the website's score cards (Discord's /recent, /best, announcements),
/// from the pp calculator, which caches each map and mod combination.
/// </summary>
public sealed class ModStarRatings : IStarRatings
{
    private readonly IPerformanceCalculator _calculator;

    public ModStarRatings(IPerformanceCalculator calculator)
    {
        _calculator = calculator;
    }

    public async Task<double?> GetAsync(int beatmapId, int ruleset, IReadOnlyList<string> mods, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mods);
        // Only the mods that change the rating count (lazer-only ones and mod settings are left out).
        if (_calculator is not IAccuracyPerformance withStars || NowPlaying.ParseAcronyms(string.Concat(mods.Where(OsuWebCommunity.StarRatingMods.Contains))) is not { } legacy)
            return null;
        try
        {
            return await withStars.StarRatingAsync(beatmapId, ruleset, (int)legacy, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            return null; // the .osu couldn't be fetched
        }
    }
}
