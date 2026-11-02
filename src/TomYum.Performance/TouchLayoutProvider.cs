using Microsoft.Extensions.Caching.Memory;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Scoring;

namespace TomYum.Performance;

/// <summary>A circle (or slider head) a touchscreen tap can land on: when it's hit and where, in osu!px.</summary>
/// <param name="StartTime">When it's hit, in the map's time (ms).</param>
/// <param name="X">Where, with stacking and Hard Rock's flip.</param>
/// <param name="Y">Where, with stacking and Hard Rock's flip.</param>
public readonly record struct TouchCircle(double StartTime, float X, float Y);

/// <summary>A time span in the map's time (ms).</summary>
/// <param name="Start">Its start.</param>
/// <param name="End">Its end.</param>
public readonly record struct TouchSpan(double Start, double End);

/// <summary>
/// What osu!stable's touchscreen check (<c>Player.UpdateTouchDeviceFlag</c>) reads from the map, as played with the
/// score's mods: the circles in order of time, their radius, how early they can be hit (<see cref="PreEmpt"/>) and
/// how late (<see cref="HitWindow50"/>), the breaks and spinners (where it doesn't count), and the speed (its 74 ms
/// press window is in real time, replay frames are in the map's).
/// </summary>
/// <param name="Circles">The circles and slider heads, in order of time.</param>
/// <param name="Radius">Their radius in osu!px.</param>
/// <param name="PreEmpt">How long before its time a circle appears (ms).</param>
/// <param name="HitWindow50">How long after its time a circle can still be hit (ms).</param>
/// <param name="Breaks">The breaks.</param>
/// <param name="Spinners">The spinners.</param>
/// <param name="Rate">The speed the map is played at (1.5 with DT/NC, 0.75 with HT).</param>
public sealed record TouchLayout(
    IReadOnlyList<TouchCircle> Circles,
    float Radius,
    double PreEmpt,
    double HitWindow50,
    IReadOnlyList<TouchSpan> Breaks,
    IReadOnlyList<TouchSpan> Spinners,
    double Rate);

/// <summary>Where an osu! map's circles are for the touchscreen check, from the same <c>.osu</c> files the pp uses.</summary>
public sealed class TouchLayoutProvider
{
    private readonly OsuFileCache _files;
    private readonly IMemoryCache _cache;

    /// <summary>Creates the provider.</summary>
    /// <param name="files">The <c>.osu</c> files.</param>
    /// <param name="cache">Keeps a map's layout for a while (players retry).</param>
    public TouchLayoutProvider(OsuFileCache files, IMemoryCache cache)
    {
        _files = files;
        _cache = cache;
    }

    /// <summary>The layout of <paramref name="beatmapId"/> in osu! with stable's <paramref name="legacyMods"/>, or null when the map can't be read.</summary>
    /// <param name="beatmapId">The map.</param>
    /// <param name="legacyMods">The play's mods (stable's numbers).</param>
    /// <param name="cancellationToken">Cancels the download of the map.</param>
    public async Task<TouchLayout?> GetAsync(int beatmapId, int legacyMods, CancellationToken cancellationToken = default)
    {
        // Only the mods that move or time the circles matter: EZ, HR, DT/NC, HT.
        var relevant = legacyMods & (int)(LegacyMods.Easy | LegacyMods.HardRock | LegacyMods.DoubleTime | LegacyMods.Nightcore | LegacyMods.HalfTime);
        try
        {
            return await _cache.GetOrCreateAsync($"touch-layout:{beatmapId}:{relevant}:{_files.Stamp(beatmapId)}", async entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromMinutes(10);
                var bytes = await _files.GetAsync(beatmapId, cancellationToken).ConfigureAwait(false);
                using var stream = new MemoryStream(bytes, writable: false);
                return Build(stream, relevant);
            }).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // a map that can't be read only means the check falls back
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>The layout of the <c>.osu</c> file in <paramref name="osuFile"/> with stable's <paramref name="legacyMods"/>.</summary>
    /// <param name="osuFile">The map.</param>
    /// <param name="legacyMods">The play's mods (stable's numbers).</param>
    public static TouchLayout Build(Stream osuFile, int legacyMods)
    {
        var ruleset = new OsuRuleset();
        var mods = OfficialPerformanceCalculator.StableMods(ruleset, legacyMods);
        var playable = new OfficialPerformanceCalculator.StreamedWorkingBeatmap(osuFile, ruleset)
            .GetPlayableBeatmap(ruleset.RulesetInfo, mods, CancellationToken.None);

        var circles = new List<TouchCircle>();
        var spinners = new List<TouchSpan>();
        float radius = 0;
        double preEmpt = 0, window50 = 0;
        foreach (var hitObject in playable.HitObjects.OfType<OsuHitObject>())
        {
            if (hitObject is Spinner spinner)
            {
                spinners.Add(new TouchSpan(spinner.StartTime, spinner.EndTime));
                continue;
            }
            circles.Add(new TouchCircle(hitObject.StartTime, hitObject.StackedPosition.X, hitObject.StackedPosition.Y));
            radius = (float)hitObject.Radius;
            preEmpt = hitObject.TimePreempt;
            window50 = hitObject.HitWindows.WindowFor(HitResult.Meh);
        }
        circles.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));

        var rate = mods.OfType<ModRateAdjust>().Select(m => m.SpeedChange.Value).DefaultIfEmpty(1.0).First();
        var breaks = playable.Breaks.Select(b => new TouchSpan(b.StartTime, b.EndTime)).ToList();
        return new TouchLayout(circles, radius, preEmpt, window50, breaks, spinners, rate);
    }
}
