using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using TomYum.Server.Beatmaps;

namespace TomYum.Server.Anticheat;

/// <summary>
/// The top plays on osu! itself for a map, with their replays, so a replay stolen from osu! is
/// caught too (the production circleguard did the same). osu!'s API v1: <c>get_scores</c> for the
/// top 5, <c>get_replay</c> for each (bare frames). osu! allows ten replays a minute, so replays are
/// fetched one at a time with a pause between, and kept on disk (<c>data/circleguard/official</c>);
/// the top lists are kept for a day.
/// </summary>
public sealed class OfficialReplays
{
    public const int Top = 5;

    private static readonly TimeSpan ListFor = TimeSpan.FromHours(24);
    private static readonly TimeSpan ReplayGap = TimeSpan.FromSeconds(7);

    private readonly HttpClient _http;
    private readonly OsuApiOptions _api;
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly ILogger<OfficialReplays> _logger;
    private readonly ConcurrentDictionary<(int BeatmapId, int Ruleset), (DateTimeOffset At, IReadOnlyList<OfficialScore> Scores, int Limit)> _lists = new();
    // osu!'s limit is per key, so the pace is kept process-wide.
    private static readonly SemaphoreSlim ReplayGate = new(1, 1);
    private static DateTimeOffset _lastReplay = DateTimeOffset.MinValue;

    public OfficialReplays(HttpClient http, OsuApiOptions api, IConfiguration configuration, TimeProvider time, ILogger<OfficialReplays> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _http = http;
        _api = api;
        _time = time;
        _logger = logger;
        _directory = Path.Combine(configuration["Circleguard:CachePath"] ?? "data/circleguard", "official");
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_api.ApiKey);

    private sealed record OfficialScore(long ScoreId, string Username, int Mods, bool ReplayAvailable, int Count300, int Count100, int Count50, int CountMiss);

    /// <summary>
    /// osu!'s top plays on the map (up to <paramref name="count"/>, those with replays), as references; empty when
    /// osu! can't be asked. <paramref name="progress"/> is told (done, of) as each replay is looked at.
    /// </summary>
    public async Task<IReadOnlyList<CircleguardReference>> TopAsync(
        int beatmapId, int ruleset, int count = Top, Action<int, int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || count <= 0)
            return [];
        var references = new List<CircleguardReference>();
        var withReplays = (await ListAsync(beatmapId, ruleset, Math.Clamp(count, 1, 100), cancellationToken).ConfigureAwait(false))
            .Take(count).Where(s => s.ReplayAvailable).ToList();
        progress?.Invoke(0, withReplays.Count);
        foreach (var (score, index) in withReplays.Select((s, i) => (s, i)))
        {
            if (await ReplayAsync(score.ScoreId, ruleset, cancellationToken).ConfigureAwait(false) is { } frames)
                references.Add(new CircleguardReference(score.ScoreId, score.Username + " (osu!)", score.Mods, frames, FramesOnly: true,
                    score.Count300, score.Count100, score.Count50, score.CountMiss));
            progress?.Invoke(index + 1, withReplays.Count);
        }
        return references;
    }

    private async Task<IReadOnlyList<OfficialScore>> ListAsync(int beatmapId, int ruleset, int count, CancellationToken cancellationToken)
    {
        var key = (beatmapId, ruleset);
        if (_lists.TryGetValue(key, out var cached) && _time.GetUtcNow() - cached.At < ListFor && cached.Limit >= count)
            return cached.Scores;

        var url = string.Create(CultureInfo.InvariantCulture, $"https://osu.ppy.sh/api/get_scores?k={Uri.EscapeDataString(_api.ApiKey!)}&b={beatmapId}&m={ruleset}&limit={count}");
        try
        {
            using var document = JsonDocument.Parse(await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false));
            var scores = document.RootElement.ValueKind != JsonValueKind.Array ? [] : document.RootElement.EnumerateArray().Select(s => new OfficialScore(
                long.Parse(s.GetProperty("score_id").GetString()!, CultureInfo.InvariantCulture),
                s.GetProperty("username").GetString() ?? string.Empty,
                int.Parse(s.GetProperty("enabled_mods").GetString()!, CultureInfo.InvariantCulture),
                s.GetProperty("replay_available").GetString() == "1",
                Count(s, "count300"), Count(s, "count100"), Count(s, "count50"), Count(s, "countmiss"))).ToList();
            _lists[key] = (_time.GetUtcNow(), scores, count);
            return scores;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogInformation(ex, "circleguard: couldn't read osu!'s top plays on beatmap {BeatmapId}.", beatmapId);
            return [];
        }
    }

    /// <summary>A count from osu!'s v1 answer (a number in a string); 0 when missing.</summary>
    private static int Count(JsonElement score, string name) =>
        score.TryGetProperty(name, out var value) && int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0;

    /// <summary>One official replay's frames, from disk or osu! (paced to osu!'s ten a minute).</summary>
    private async Task<byte[]?> ReplayAsync(long scoreId, int ruleset, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, scoreId.ToString(CultureInfo.InvariantCulture) + ".lzma");
        if (File.Exists(path))
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        await ReplayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _lastReplay + ReplayGap - _time.GetUtcNow();
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            _lastReplay = _time.GetUtcNow();

            var url = string.Create(CultureInfo.InvariantCulture, $"https://osu.ppy.sh/api/get_replay?k={Uri.EscapeDataString(_api.ApiKey!)}&s={scoreId}&m={ruleset}");
            using var document = JsonDocument.Parse(await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("content", out var content) || content.GetString() is not { Length: > 0 } base64)
                return null;
            var frames = Convert.FromBase64String(base64);
            Directory.CreateDirectory(_directory);
            await File.WriteAllBytesAsync(path, frames, cancellationToken).ConfigureAwait(false);
            return frames;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException)
        {
            _logger.LogInformation(ex, "circleguard: couldn't fetch osu! replay {ScoreId}.", scoreId);
            return null;
        }
        finally
        {
            ReplayGate.Release();
        }
    }
}
