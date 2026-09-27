using TomYum.Domain.Entities;
using TomYum.Domain.Enums;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// Translation rules from TomYum's <see cref="Beatmap"/> to osu-web's beatmap tables. Free of I/O so
/// the rules are unit-testable; <see cref="OsuWebBeatmapMirror"/> applies them.
/// </summary>
public static class OsuWebBeatmaps
{
    /// <summary>osu-web's beatmap and set ids are <c>MEDIUMINT UNSIGNED</c>.</summary>
    public const int MaxId = 16_777_215;

    /// <summary>Length of osu-web's <c>artist</c>/<c>title</c>/<c>creator</c>/<c>version</c> columns.</summary>
    public const int NameLength = 80;

    /// <summary>
    /// osu-web's <c>approved</c> value for <paramref name="beatmap"/>, or <c>null</c> when the map
    /// shouldn't be on the site (not submitted). Uses the raw osu! API value when it still agrees with
    /// the effective status, so graveyard/WIP survive; a frozen (staff-set) status always wins.
    /// </summary>
    public static sbyte? ToApproved(Beatmap beatmap)
    {
        ArgumentNullException.ThrowIfNull(beatmap);

        if (!beatmap.StatusFrozen
            && beatmap.OsuApiStatus is { } api and >= -2 and <= 4
            && RankedStatusExtensions.FromOsuApi(api) == beatmap.Status)
        {
            return (sbyte)api;
        }

        return beatmap.Status switch
        {
            RankedStatus.NotSubmitted => null,
            RankedStatus.Ranked => 1,
            RankedStatus.Approved => 2,
            RankedStatus.Qualified => 3,
            RankedStatus.Loved => 4,
            // Pending, and UpdateAvailable (the client's copy is outdated, the map itself isn't).
            _ => 0,
        };
    }

    /// <summary>osu!'s precedence of <c>approved</c> values for a whole set: ranked, approved, loved, qualified, pending, WIP, graveyard.</summary>
    private static readonly sbyte[] SetStatusPrecedence = [1, 2, 4, 3, 0, -1, -2];

    /// <summary>A set's <c>approved</c> from its difficulties' values: the one that comes first in osu!'s precedence.</summary>
    public static sbyte SetStatusFor(IEnumerable<sbyte> difficultyStatuses)
    {
        ArgumentNullException.ThrowIfNull(difficultyStatuses);

        var present = difficultyStatuses.ToHashSet();
        foreach (var status in SetStatusPrecedence)
        {
            if (present.Contains(status))
                return status;
        }
        return 0;
    }

    /// <summary>Whether an <c>approved</c> value has a ranking date on osu-web (ranked, approved, qualified, loved).</summary>
    public static bool HasApprovedDate(sbyte approved) => approved > 0;

    /// <summary>Cuts <paramref name="value"/> to <paramref name="length"/> characters without splitting a surrogate pair.</summary>
    public static string Truncate(string value, int length = NameLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length <= length)
            return value;
        var cut = char.IsHighSurrogate(value[length - 1]) ? length - 1 : length;
        return value[..cut];
    }

    /// <summary>
    /// A UTC time osu-web's <c>TIMESTAMP</c> columns accept. Unknown (default) or out-of-range
    /// dates become <paramref name="fallback"/>.
    /// </summary>
    public static DateTime ToTimestamp(DateTimeOffset value, DateTime fallback)
    {
        var utc = value.UtcDateTime;
        return utc.Year is >= 1971 and <= 2037 ? utc : fallback;
    }
}
