namespace TomYum.Persistence.OsuWeb;

/// <summary>A map's star rating in a ruleset with mods (the server's difficulty calculator).</summary>
public interface IStarRatings
{
    /// <summary>The star rating, or null when it can't be worked out (the card keeps the map's own).</summary>
    Task<double?> GetAsync(int beatmapId, int ruleset, IReadOnlyList<string> mods, CancellationToken cancellationToken = default);
}
