using System.IO;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics.Textures;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.Beatmaps.Legacy;
using osu.Game.IO;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Taiko;
using osu.Game.Scoring;
using osu.Game.Skinning;

namespace TomYum.Performance;

/// <summary>
/// The "official" pp backend: computes pp in-process with the official ppy.osu.Game.Rulesets.*
/// packages (osu!lazer algorithms). Downloads the .osu from osu.ppy.sh, caches difficulty
/// attributes 30 min by beatmapId+rulesetId+mods, then runs the performance calculator.
/// One of the pluggable backends behind RoutingPerformanceCalculator (ADR-0005 / ADR-0010).
/// </summary>
public sealed class OfficialPerformanceCalculator : IPerformanceCalculator, IAccuracyPerformance, IBeatmapDifficulty, IPartialPerformance
{
    private readonly OsuFileCache _files;
    private readonly IMemoryCache _cache;
    private readonly ILogger<OfficialPerformanceCalculator> _log;

    public OfficialPerformanceCalculator(OsuFileCache files, IMemoryCache cache, ILogger<OfficialPerformanceCalculator> log)
    {
        _files = files;
        _cache = cache;
        _log = log;
    }

    public async Task<double> CalculateAsync(PerformanceRequest request, CancellationToken cancellationToken = default)
    {
        var ruleset = GetRuleset(request.RulesetId);
        var mods = WithSpeed(StableMods(ruleset, request.Mods), request.SpeedChange);

        var difficulty = await DifficultyAsync(request.BeatmapId, request.RulesetId, ModsKey(request), ruleset, mods, cancellationToken).ConfigureAwait(false);
        if (difficulty?.Attributes is not { } diffAttribs)
            return 0.0;

        var scoreInfo = BuildScoreInfo(request, ruleset, mods, difficulty.Beatmap);
        var perfAttribs = ruleset.CreatePerformanceCalculator()?.Calculate(scoreInfo, diffAttribs);
        var pp = perfAttribs?.Total ?? 0.0;

        // Diagnostic: surface the computed star rating alongside the resulting pp. This is the
        // fastest way to tell a genuine calc problem (wrong SR) from stale stored/displayed
        // values — a healthy 1★ catch convert logs SR≈1.0 / pp≈12 here even if the DB row and
        // in-game screen still show an old inflated number. Enable via LogLevel Debug for
        // "TomYum.Performance.OfficialPerformanceCalculator".
        _log.LogDebug(
            "PP calc: beatmap {BeatmapId} ruleset {RulesetId} mods {Mods} → SR={StarRating:F3} acc={Accuracy:P2} combo={MaxCombo} = {Pp:F2}pp",
            request.BeatmapId, request.RulesetId, request.Mods, diffAttribs.StarRating, scoreInfo.Accuracy, request.MaxCombo, pp);

        return pp;
    }

    /// <summary>
    /// osu! only: pp for a full combo at each accuracy, with the misses-free hit counts that give it
    /// (all 100s beyond the 300s, as ppy's own "pp if FC" does).
    /// </summary>
    public async Task<IReadOnlyList<double>?> ForAccuraciesAsync(int beatmapId, int rulesetId, int mods, IReadOnlyList<double> accuracies, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accuracies);
        if (rulesetId != 0)
            return null;

        var ruleset = GetRuleset(0);
        var modList = StableMods(ruleset, mods);
        var difficulty = await DifficultyAsync(beatmapId, 0, mods, ruleset, modList, cancellationToken).ConfigureAwait(false);
        if (difficulty?.Attributes is not osu.Game.Rulesets.Osu.Difficulty.OsuDifficultyAttributes attributes)
            return null;

        var objects = attributes.HitCircleCount + attributes.SliderCount + attributes.SpinnerCount;
        var calculator = ruleset.CreatePerformanceCalculator();
        if (objects == 0 || calculator is null)
            return null;

        var results = new List<double>(accuracies.Count);
        foreach (var accuracy in accuracies)
        {
            // acc = (300·n300 + 100·n100) / (300·n)  =>  n100 = 1.5·n·(1 − acc)
            var n100 = (int)Math.Clamp(Math.Round(1.5 * objects * (1 - Math.Clamp(accuracy, 0, 1))), 0, objects);
            var request = new PerformanceRequest(beatmapId, 0, mods, attributes.MaxCombo, objects - n100, n100, 0, 0, 0, 0, Passed: true);
            results.Add(calculator.Calculate(BuildScoreInfo(request, ruleset, modList, difficulty.Beatmap), attributes).Total);
        }
        return results;
    }

    /// <summary>
    /// A failed play's pp: the difficulty after the objects it reached (hits of every kind, counted),
    /// and the play's own hits and combo on that part.
    /// </summary>
    public async Task<double?> PartialAsync(PerformanceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RulesetId is < 0 or > 3)
            return null;
        var reached = request.Count300 + request.Count100 + request.Count50 + request.CountMiss
                      + (request.RulesetId is 2 or 3 ? request.CountKatu : 0) + (request.RulesetId == 3 ? request.CountGeki : 0);
        if (reached <= 0)
            return 0;

        var ruleset = GetRuleset(request.RulesetId);
        var mods = WithSpeed(StableMods(ruleset, request.Mods), request.SpeedChange);
        var timed = await _cache.GetOrCreateAsync(
            $"diff:timed:{request.BeatmapId}:{request.RulesetId}:{ModsKey(request)}:{_files.Stamp(request.BeatmapId)}", async entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromMinutes(10);
                var bytes = await _files.GetAsync(request.BeatmapId, cancellationToken).ConfigureAwait(false);
                using var stream = new MemoryStream(bytes, writable: false);
                var working = new StreamedWorkingBeatmap(stream, ruleset);
                return (Attributes: ruleset.CreateDifficultyCalculator(working).CalculateTimed(mods), working.BeatmapInfo);
            }).ConfigureAwait(false);
        if (timed.Attributes.Count == 0)
            return null;

        var attributes = timed.Attributes[Math.Min(reached, timed.Attributes.Count) - 1].Attributes;
        var scoreInfo = BuildScoreInfo(request with { Passed = true }, ruleset, mods, timed.BeatmapInfo);
        return ruleset.CreatePerformanceCalculator()?.Calculate(scoreInfo, attributes).Total;
    }

    public async Task<double?> StarRatingAsync(int beatmapId, int rulesetId, int mods, CancellationToken cancellationToken = default)
    {
        if (rulesetId is < 0 or > 3)
            return null;
        var ruleset = GetRuleset(rulesetId);
        var difficulty = await DifficultyAsync(beatmapId, rulesetId, mods, ruleset, StableMods(ruleset, mods), cancellationToken).ConfigureAwait(false);
        return difficulty?.Attributes.StarRating;
    }

    public async Task<BeatmapDifficulty?> DifficultyAsync(int beatmapId, int rulesetId, CancellationToken cancellationToken = default)
    {
        if (rulesetId is < 0 or > 3)
            return null;
        var ruleset = GetRuleset(rulesetId);
        var difficulty = await DifficultyAsync(beatmapId, rulesetId, 0, ruleset, StableMods(ruleset, 0), cancellationToken).ConfigureAwait(false);
        return difficulty is null ? null : new BeatmapDifficulty(difficulty.Attributes.StarRating, difficulty.Attributes.MaxCombo);
    }

    /// <summary>A map's difficulty attributes, and the map itself (the pp formula reads its AR/OD/CS/HP).</summary>
    private sealed record Difficulty(DifficultyAttributes Attributes, BeatmapInfo Beatmap);

    private Task<Difficulty?> DifficultyAsync(int beatmapId, int rulesetId, int legacyMods, Ruleset ruleset, osu.Game.Rulesets.Mods.Mod[] mods, CancellationToken cancellationToken) =>
        DifficultyAsync(beatmapId, rulesetId, legacyMods.ToString(System.Globalization.CultureInfo.InvariantCulture), ruleset, mods, cancellationToken);

    private Task<Difficulty?> DifficultyAsync(int beatmapId, int rulesetId, string modsKey, Ruleset ruleset, osu.Game.Rulesets.Mods.Mod[] mods, CancellationToken cancellationToken) =>
        _cache.GetOrCreateAsync($"diff:official:{beatmapId}:{rulesetId}:{modsKey}:{_files.Stamp(beatmapId)}", async entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromMinutes(30);
            var bytes = await _files.GetAsync(beatmapId, cancellationToken).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes, writable: false);
            var working = new StreamedWorkingBeatmap(stream, ruleset);
            return new Difficulty(ruleset.CreateDifficultyCalculator(working).Calculate(mods), working.BeatmapInfo);
        });

    /// <summary>
    /// A stable score's mods as lazer's calculators must see them: the legacy bits plus Classic, as
    /// ppy's score processor gives every stable score. Without Classic the osu! calculator expects
    /// lazer's slider-end and tick judgements, which stable scores don't have, and treats them as
    /// missed (an SS of a 7★ map came out at 460pp instead of 598pp).
    /// </summary>
    /// <summary>The cache key's part for a request's mods: the legacy bits, and a rate mod's own speed.</summary>
    private static string ModsKey(PerformanceRequest request) =>
        request.SpeedChange is { } speed
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{request.Mods}@{speed:0.###}")
            : request.Mods.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// <paramref name="mods"/> with their rate mod (DT, NC, HT, DC) at <paramref name="speed"/>: a lazer play
    /// can set its own (DT at 1.6x, say), which the legacy bits can't say.
    /// </summary>
    public static osu.Game.Rulesets.Mods.Mod[] WithSpeed(osu.Game.Rulesets.Mods.Mod[] mods, double? speed)
    {
        if (speed is not { } value)
            return mods;
        foreach (var mod in mods.OfType<osu.Game.Rulesets.Mods.ModRateAdjust>())
            mod.SpeedChange.Value = Math.Clamp(value, mod.SpeedChange.MinValue, mod.SpeedChange.MaxValue);
        return mods;
    }

    public static osu.Game.Rulesets.Mods.Mod[] StableMods(Ruleset ruleset, int legacyMods) =>
        ruleset.ConvertFromLegacyMods((LegacyMods)legacyMods).Append(ruleset.CreateMod<osu.Game.Rulesets.Mods.ModClassic>()!).ToArray();

    /// <remarks>
    /// <paramref name="beatmap"/> must be the played map: the pp formula reads its approach rate,
    /// overall difficulty and so on from the score's map, and a score without one is scored as a
    /// default AR5/OD5 map (far too little pp on most maps).
    /// </remarks>
    internal static ScoreInfo BuildScoreInfo(PerformanceRequest req, Ruleset ruleset, osu.Game.Rulesets.Mods.Mod[] mods, BeatmapInfo beatmap)
    {
        var scoreInfo = new ScoreInfo(beatmap, ruleset.RulesetInfo)
        {
            Ruleset = ruleset.RulesetInfo,
            Mods = mods,
            MaxCombo = req.MaxCombo,
            Passed = req.Passed,
            // All bancho-submitted scores come from osu!stable (legacy format).
            // Without this flag the lazer PP calculator expects SliderTailHit /
            // LargeTickHit statistics that stable never sends, treating every
            // slider end/tick as missed and gutting aim PP.
            IsLegacyScore = true,
        };

        // Map legacy hit counts to the HitResult statistics the PP calculator expects.
        // Logic mirrors osu/osu.Game/Scoring/Legacy/ScoreInfoExtensions.cs SetCount* methods.
        scoreInfo.Statistics[HitResult.Great] = req.Count300;
        scoreInfo.Statistics[HitResult.Miss] = req.CountMiss;

        switch (req.RulesetId)
        {
            case 0: // osu!
                scoreInfo.Statistics[HitResult.Ok] = req.Count100;
                scoreInfo.Statistics[HitResult.Meh] = req.Count50;
                break;

            case 1: // taiko  — geki + katu both fold into LargeBonus
                scoreInfo.Statistics[HitResult.Ok] = req.Count100;
                scoreInfo.Statistics[HitResult.LargeBonus] =
                    scoreInfo.Statistics.GetValueOrDefault(HitResult.LargeBonus) + req.CountGeki + req.CountKatu;
                break;

            case 2: // catch
                scoreInfo.Statistics[HitResult.LargeTickHit] = req.Count100;
                scoreInfo.Statistics[HitResult.SmallTickHit] = req.Count50;
                scoreInfo.Statistics[HitResult.SmallTickMiss] = req.CountKatu;
                break;

            case 3: // mania
                scoreInfo.Statistics[HitResult.Perfect] = req.CountGeki;
                scoreInfo.Statistics[HitResult.Ok] = req.Count100;
                scoreInfo.Statistics[HitResult.Meh] = req.Count50;
                scoreInfo.Statistics[HitResult.Good] = req.CountKatu;
                break;
        }

        // Compute accuracy: the PP calculator reads scoreInfo.Accuracy directly.
        // Formula matches the standard osu! accuracy calculation per ruleset.
        var totalHits = req.Count300 + req.Count100 + req.Count50 + req.CountMiss;
        if (totalHits > 0)
        {
            scoreInfo.Accuracy = req.RulesetId switch
            {
                0 => (req.Count300 * 300.0 + req.Count100 * 100.0 + req.Count50 * 50.0) / (totalHits * 300.0),
                1 => (req.Count300 + req.Count100 * 0.5) / totalHits,
                // catch: missed droplets (nKatu) count against accuracy
                2 => (double)(req.Count300 + req.Count100 + req.Count50)
                     / (req.Count300 + req.Count100 + req.Count50 + req.CountKatu + req.CountMiss),
                // mania: all note types (MAX/300g + 200) are in the denominator
                3 => (req.CountGeki * 300.0 + req.Count300 * 300.0 + req.CountKatu * 200.0 + req.Count100 * 100.0 + req.Count50 * 50.0)
                     / ((req.CountGeki + req.Count300 + req.CountKatu + req.Count100 + req.Count50 + req.CountMiss) * 300.0),
                _ => 0.0,
            };
        }

        return scoreInfo;
    }

    private static Ruleset GetRuleset(int id) => id switch
    {
        0 => new OsuRuleset(),
        1 => new TaikoRuleset(),
        2 => new CatchRuleset(),
        3 => new ManiaRuleset(),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unsupported ruleset id"),
    };

    // Minimal WorkingBeatmap that wraps a downloaded .osu stream, same pattern as
    // osu-queue-score-statistics/Stores/StreamedWorkingBeatmap.cs.
    internal sealed class StreamedWorkingBeatmap : WorkingBeatmap
    {
        private readonly Beatmap _beatmap;

        public StreamedWorkingBeatmap(Stream stream, Ruleset targetRuleset) : this(new LineBufferedReader(stream), targetRuleset)
        {
            stream.Dispose();
        }

        private StreamedWorkingBeatmap(LineBufferedReader reader, Ruleset targetRuleset) : this(osu.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader), targetRuleset)
        {
            reader.Dispose();
        }

        private StreamedWorkingBeatmap(Beatmap beatmap, Ruleset targetRuleset) : base(beatmap.BeatmapInfo, null)
        {
            _beatmap = beatmap;
            // Record the target ruleset on BeatmapInfo for consistency (a Mode-0 map being
            // calculated as catch reports the target ruleset). NOTE: this does NOT influence
            // conversion or star rating — GetPlayableBeatmap converts purely from the ruleset
            // passed to the difficulty calculator, so leaving the native mode here yields
            // identical attributes. The earlier "native ruleset ⇒ 4★ inflation" theory was
            // wrong: this map computes 1.02★ either way, in both 2026.605.0 and 2026.702.1.
            // See memories/changelogs/CHANGELOG.md (2026-07-07 catch-convert PP investigation).
            beatmap.BeatmapInfo.Ruleset = targetRuleset.RulesetInfo;
        }

        protected override IBeatmap GetBeatmap() => _beatmap;
        public override Texture GetBackground() => throw new NotImplementedException();
        protected override Track GetBeatmapTrack() => throw new NotImplementedException();
        protected override ISkin GetSkin() => throw new NotImplementedException();
        public override Stream GetStream(string storagePath) => throw new NotImplementedException();
    }
}
