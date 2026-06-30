using System.Globalization;
using System.IO;
using System.Net.Http;

using Microsoft.Extensions.Caching.Memory;
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
/// Real PP calculator using the official ppy.osu.Game.Rulesets.* packages in-process.
/// Downloads the .osu file from osu.ppy.sh, calculates difficulty attributes (cached by
/// beatmapId+rulesetId+mods), then computes performance attributes per ADR-0005.
/// </summary>
public sealed class OsuPerformanceCalculator : IPerformanceCalculator
{
    private static readonly System.Text.CompositeFormat BeatmapDownloadUrl = System.Text.CompositeFormat.Parse("https://osu.ppy.sh/osu/{0}");

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;

    public OsuPerformanceCalculator(HttpClient http, IMemoryCache cache)
    {
        _http = http;
        _cache = cache;
    }

    public async Task<double> CalculateAsync(PerformanceRequest request, CancellationToken cancellationToken = default)
    {
        var ruleset = GetRuleset(request.RulesetId);
        var mods = ruleset.ConvertFromLegacyMods((LegacyMods)request.Mods).ToArray();

        var cacheKey = $"diff:{request.BeatmapId}:{request.RulesetId}:{request.Mods}";
        var diffAttribs = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromMinutes(30);
            var url = string.Format(CultureInfo.InvariantCulture, BeatmapDownloadUrl, request.BeatmapId);
            using var stream = await _http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
            var working = new StreamedWorkingBeatmap(stream);
            return ruleset.CreateDifficultyCalculator(working).Calculate(mods);
        }).ConfigureAwait(false);

        if (diffAttribs is null)
            return 0.0;

        var scoreInfo = BuildScoreInfo(request, ruleset, mods);
        var perfAttribs = ruleset.CreatePerformanceCalculator()?.Calculate(scoreInfo, diffAttribs);
        return perfAttribs?.Total ?? 0.0;
    }

    internal static ScoreInfo BuildScoreInfo(PerformanceRequest req, Ruleset ruleset, osu.Game.Rulesets.Mods.Mod[] mods)
    {
        var scoreInfo = new ScoreInfo
        {
            Ruleset = ruleset.RulesetInfo,
            Mods = mods,
            MaxCombo = req.MaxCombo,
            Passed = req.Passed,
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
    private sealed class StreamedWorkingBeatmap : WorkingBeatmap
    {
        private readonly Beatmap _beatmap;

        public StreamedWorkingBeatmap(Stream stream) : this(new LineBufferedReader(stream))
        {
            stream.Dispose();
        }

        private StreamedWorkingBeatmap(LineBufferedReader reader) : this(osu.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader))
        {
            reader.Dispose();
        }

        private StreamedWorkingBeatmap(Beatmap beatmap) : base(beatmap.BeatmapInfo, null)
        {
            _beatmap = beatmap;
            beatmap.BeatmapInfo.Ruleset = beatmap.BeatmapInfo.Ruleset.OnlineID switch
            {
                0 => new OsuRuleset().RulesetInfo,
                1 => new TaikoRuleset().RulesetInfo,
                2 => new CatchRuleset().RulesetInfo,
                3 => new ManiaRuleset().RulesetInfo,
                _ => beatmap.BeatmapInfo.Ruleset,
            };
        }

        protected override IBeatmap GetBeatmap() => _beatmap;
        public override Texture GetBackground() => throw new NotImplementedException();
        protected override Track GetBeatmapTrack() => throw new NotImplementedException();
        protected override ISkin GetSkin() => throw new NotImplementedException();
        public override Stream GetStream(string storagePath) => throw new NotImplementedException();
    }
}
