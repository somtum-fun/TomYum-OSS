using System.Globalization;
using TomYum.Performance;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Beatmaps;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Anticheat;

/// <summary>What a scan found, and what the play was compared with.</summary>
/// <param name="Verdict">circleguard's answer.</param>
/// <param name="LocalReferences">Top plays from this server it was compared with.</param>
/// <param name="OfficialReferences">Top plays from osu! it was compared with.</param>
public sealed record CircleguardScan(CircleguardVerdict Verdict, int LocalReferences, int OfficialReferences);

/// <summary>
/// Sends one website score (stable or lazer) to circleguard, with the map and the replays to
/// compare it against: the map's top plays here, and osu!'s top plays on official maps (stolen
/// replays). Used by <see cref="CircleguardWatch"/> for every new pass and by Discord's <c>/scan</c>.
/// </summary>
public sealed class CircleguardScanner
{
    private readonly CircleguardOptions _options;
    private readonly CircleguardClient _client;
    private readonly OfficialReplays _official;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CircleguardScanner> _logger;

    public CircleguardScanner(CircleguardOptions options, CircleguardClient client, OfficialReplays official, IConfiguration configuration, ILogger<CircleguardScanner> logger)
    {
        _options = options;
        _client = client;
        _official = official;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Whether osu!'s replays can be fetched (an osu! API key is set).</summary>
    public bool CanCompareOfficial => _options.CompareOfficial && _official.IsAvailable;

    /// <summary>Where a website score's replay is: TomYum's file for stable scores, the spectator's for lazer; null when unknown.</summary>
    public async Task<string?> ReplayPathAsync(OsuWebCommunity community, ScoreCard score, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(community);
        ArgumentNullException.ThrowIfNull(score);
        if (!score.IsStable)
            return _options.LazerReplaysPath.Length == 0 ? null : Path.Combine(_options.LazerReplaysPath, score.ScoreId.ToString(CultureInfo.InvariantCulture));
        return await community.GetStableScoreAsync(score.ScoreId, cancellationToken).ConfigureAwait(false) is { } stable
            ? ReplayFiles.PathFor(_configuration, stable.StableScoreId)
            : null;
    }

    /// <summary>
    /// Scans <paramref name="score"/> (its replay at <paramref name="path"/>), compared with up to
    /// <paramref name="localReferences"/> top plays here and <paramref name="officialReferences"/> from
    /// osu! (osu! maps only; osu!'s replays are fetched at ten a minute and kept).
    /// </summary>
    public async Task<CircleguardScan> ScanAsync(
        IServiceProvider services, OsuWebCommunity community, ScoreCard score, string path, int localReferences, int officialReferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(community);
        ArgumentNullException.ThrowIfNull(score);
        var replay = await PlayAsync(services, community, score, path, cancellationToken).ConfigureAwait(false);
        byte[]? beatmap = null;
        try
        {
            beatmap = await services.GetRequiredService<OsuFileCache>().GetAsync(score.BeatmapId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _logger.LogInformation("circleguard: no .osu for beatmap {BeatmapId}; judging the replay alone.", score.BeatmapId);
        }

        var references = new List<CircleguardReference>();
        var official = 0;
        if (score.Ruleset == 0)
        {
            // The map's best here (other players), then osu!'s own top plays for official maps.
            foreach (var top in await community.GetBeatmapLeaderboardAsync(score.BeatmapId, score.Ruleset, localReferences + 3, cancellationToken).ConfigureAwait(false))
            {
                if (references.Count >= localReferences || top.UserId == score.UserId || top.ScoreId == score.ScoreId)
                    continue;
                if (await ReplayPathAsync(community, top, cancellationToken).ConfigureAwait(false) is { } topPath && File.Exists(topPath))
                {
                    var play = await PlayAsync(services, community, top, topPath, cancellationToken).ConfigureAwait(false);
                    references.Add(new CircleguardReference((long)top.ScoreId, top.Username, play.Mods, play.Data, play.FramesOnly));
                }
            }
            if (officialReferences > 0 && CanCompareOfficial && score.BeatmapId < TomYum.Domain.Entities.Beatmap.CustomIdFloor)
            {
                var fromOsu = await _official.TopAsync(score.BeatmapId, score.Ruleset, officialReferences, cancellationToken).ConfigureAwait(false);
                official = fromOsu.Count;
                references.AddRange(fromOsu);
            }
        }

        var verdict = await _client.AnalyzeAsync(replay, beatmap, references, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("circleguard: {Player}'s score {ScoreId} ({Mods}): risk {Risk}/100 {Reasons}",
            score.Username, score.ScoreId, verdict.Mods, verdict.RiskScore, string.Join("; ", verdict.Reasons));
        return new CircleguardScan(verdict, references.Count - official, official);
    }

    /// <summary>
    /// A score's replay as circleguard takes it: lazer's files are whole <c>.osr</c>s; stable's are the
    /// frames the client uploaded, so their details come from TomYum's own score.
    /// </summary>
    private static async Task<CircleguardPlay> PlayAsync(IServiceProvider services, OsuWebCommunity community, ScoreCard score, string path, CancellationToken cancellationToken)
    {
        var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!score.IsStable
            || await community.GetStableScoreAsync(score.ScoreId, cancellationToken).ConfigureAwait(false) is not { } stable
            || services.GetService<TomYum.Persistence.TomYumDbContext>() is not { } db
            || await db.Scores.FindAsync([stable.StableScoreId], cancellationToken).ConfigureAwait(false) is not { } own)
            return new CircleguardPlay(data, FramesOnly: false);

        return new CircleguardPlay(
            data, FramesOnly: true, Ruleset: own.Mode % 4, Mods: own.Mods, Username: score.Username, MaxCombo: own.MaxCombo,
            Count300: own.Count300, Count100: own.Count100, Count50: own.Count50, CountGeki: own.CountGeki, CountKatu: own.CountKatu, CountMiss: own.CountMiss);
    }
}
