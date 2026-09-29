using System.Globalization;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics.Textures;
using osu.Game.Beatmaps;
using osu.Game.IO;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Skinning;
using TomYum.Performance;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// pp for stable scores (<see cref="OfficialPerformanceCalculator"/>): a stable SS must be worth
/// about what lazer gives an SS of the same map. Stable scores carry no slider-end or tick
/// judgements; without the Classic mod ppy's calculator counts those as missed (an SS of a 7★
/// map came out at 460pp instead of ~598pp).
/// </summary>
public sealed class StablePerformanceTests : IDisposable
{
    private const int BeatmapId = 999_001;

    private readonly string _cache = Path.Combine(Path.GetTempPath(), "tomyum-pp-test-" + Guid.NewGuid().ToString("N"));

    public StablePerformanceTests()
    {
        Directory.CreateDirectory(_cache);
        File.WriteAllText(Path.Combine(_cache, $"{BeatmapId}.osu"), SliderMap());
    }

    [Fact]
    public async Task AStableSS_IsWorthAboutWhatLazerGivesAnSS()
    {
        var calculator = new OfficialPerformanceCalculator(
            new OsuFileCache(_cache, new HttpClient(), NullLogger<OsuFileCache>.Instance),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<OfficialPerformanceCalculator>.Instance);
        var (objects, maxCombo, lazerSs) = LazerSs();

        var stableSs = await calculator.CalculateAsync(new PerformanceRequest(
            BeatmapId, 0, 0, maxCombo, Count300: objects, Count100: 0, Count50: 0, CountMiss: 0, CountGeki: 0, CountKatu: 0, Passed: true));
        var fromAccuracy = (await calculator.ForAccuraciesAsync(BeatmapId, 0, 0, [1.0]))![0];

        Assert.True(lazerSs > 0);
        // Stable and lazer judge slider ticks differently, so on this tick-heavy map a stable SS is a
        // little lower (a real 7★ map: 598pp vs 602pp). Without Classic and the map it was 0.61.
        Assert.InRange(stableSs / lazerSs, 0.9, 1.01);
        Assert.Equal(stableSs, fromAccuracy, 6);
    }

    [Fact]
    public async Task AFail_IsWorthThePartItGotThrough()
    {
        var calculator = new OfficialPerformanceCalculator(
            new OsuFileCache(_cache, new HttpClient(), NullLogger<OsuFileCache>.Instance),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<OfficialPerformanceCalculator>.Instance);
        var (objects, maxCombo, _) = LazerSs();
        var whole = await calculator.CalculateAsync(new PerformanceRequest(BeatmapId, 0, 0, maxCombo, objects, 0, 0, 0, 0, 0, Passed: true));

        var half = objects / 2;
        var halfway = await calculator.PartialAsync(new PerformanceRequest(BeatmapId, 0, 0, maxCombo / 2, half, 0, 0, 0, 0, 0, Passed: false));
        var allTheWay = await calculator.PartialAsync(new PerformanceRequest(BeatmapId, 0, 0, maxCombo, objects, 0, 0, 0, 0, 0, Passed: false));

        Assert.InRange(halfway!.Value, 1, whole * 0.9);          // worth something, but less than the whole map
        Assert.Equal(whole, allTheWay!.Value, 3);                 // reaching the end is the whole map
        Assert.Equal(0, await calculator.PartialAsync(new PerformanceRequest(BeatmapId, 0, 0, 0, 0, 0, 0, 0, 0, 0, Passed: false)));
    }

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    /// <summary>The same map as lazer scores it: every judgement (slider ends and ticks included) hit.</summary>
    private static (int Objects, int MaxCombo, double Pp) LazerSs()
    {
        var ruleset = new OsuRuleset();
        using var reader = new LineBufferedReader(new MemoryStream(Encoding.UTF8.GetBytes(SliderMap())));
        var working = new Working(osu.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader));
        var attributes = ruleset.CreateDifficultyCalculator(working).Calculate([]);
        var playable = working.GetPlayableBeatmap(ruleset.RulesetInfo);

        var statistics = new Dictionary<HitResult, int>();
        void Count(HitObject hitObject)
        {
            var result = hitObject.Judgement.MaxResult;
            if (result is not HitResult.IgnoreHit and not HitResult.None)
                statistics[result] = statistics.GetValueOrDefault(result) + 1;
            foreach (var nested in hitObject.NestedHitObjects)
                Count(nested);
        }
        foreach (var hitObject in playable.HitObjects)
            Count(hitObject);

        var score = new ScoreInfo(playable.BeatmapInfo, ruleset.RulesetInfo)
        {
            Accuracy = 1,
            MaxCombo = attributes.MaxCombo,
            Passed = true,
            Statistics = new Dictionary<HitResult, int>(statistics),
            MaximumStatistics = new Dictionary<HitResult, int>(statistics),
        };
        return (playable.HitObjects.Count, attributes.MaxCombo, ruleset.CreatePerformanceCalculator()!.Calculate(score, attributes).Total);
    }

    /// <summary>Two minutes of jumps and long sliders (with ticks), so slider judgements matter.</summary>
    private static string SliderMap()
    {
        var map = new StringBuilder("""
            osu file format v14

            [General]
            AudioFilename: audio.mp3
            Mode: 0

            [Metadata]
            Title:Test
            Artist:TomYum
            Creator:TomYum
            Version:Sliders
            BeatmapID:999001
            BeatmapSetID:-1

            [Difficulty]
            HPDrainRate:5
            CircleSize:4
            OverallDifficulty:9
            ApproachRate:9.5
            SliderMultiplier:1.8
            SliderTickRate:2

            [TimingPoints]
            0,300,4,2,0,50,1,0

            [HitObjects]

            """);
        for (var i = 0; i < 400; i++)
        {
            var time = 1000 + (i * 300);
            var x = i % 2 == 0 ? 64 : 448;
            var y = (i / 2) % 2 == 0 ? 64 : 320;
            map.AppendLine(i % 3 == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{x},{y},{time},2,0,L|{512 - x}:{y},1,270")
                : string.Create(CultureInfo.InvariantCulture, $"{x},{y},{time},1,0,0:0:0:0:"));
        }
        return map.ToString();
    }

    private sealed class Working(Beatmap beatmap) : WorkingBeatmap(beatmap.BeatmapInfo, null)
    {
        protected override IBeatmap GetBeatmap() => beatmap;

        public override Texture GetBackground() => throw new NotImplementedException();

        protected override Track GetBeatmapTrack() => throw new NotImplementedException();

        protected override ISkin GetSkin() => throw new NotImplementedException();

        public override Stream GetStream(string storagePath) => throw new NotImplementedException();
    }
}
