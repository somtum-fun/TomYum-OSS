using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TomYum.Server.Anticheat;

/// <summary>Settings for the replay checks (section <c>Circleguard</c>).</summary>
public sealed class CircleguardOptions
{
    public const string SectionName = "Circleguard";

    /// <summary>The lazer-circleguard service, e.g. <c>http://circleguard:8555</c>; empty: no checks.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The service's shared key (its <c>CIRCLEGUARD_KEY</c>).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Plays with a risk at or above this are reported to staff (Discord).</summary>
    public int ReportAt { get; set; } = 15;

    /// <summary>Players whose play scores above this are restricted at once (production's rule: above 15). 101 turns it off.</summary>
    public int RestrictAbove { get; set; } = 15;

    /// <summary>Where lazer replays are (the spectator server's <c>REPLAYS_PATH</c>, mounted read-only); empty: lazer plays aren't checked.</summary>
    public string LazerReplaysPath { get; set; } = string.Empty;

    /// <summary>Compare against the top plays on osu! too (official maps; needs the osu! API key).</summary>
    public bool CompareOfficial { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Key);
}

/// <summary>
/// The play to judge: an <c>.osr</c> (lazer's), or bare frames (what the stable client uploads) with
/// the score's details.
/// </summary>
public sealed record CircleguardPlay(
    byte[] Data, bool FramesOnly, int Ruleset = 0, int Mods = 0, string Username = "", int MaxCombo = 0,
    int Count300 = 0, int Count100 = 0, int Count50 = 0, int CountGeki = 0, int CountKatu = 0, int CountMiss = 0);

/// <summary>A top play to compare against: an <c>.osr</c>, or bare frames (stable's, the osu! API's).</summary>
public sealed record CircleguardReference(long ScoreId, string Username, int Mods, byte[] Data, bool FramesOnly);

/// <summary>The closest top play.</summary>
public sealed record CircleguardMatch(
    [property: JsonPropertyName("score_id")] long ScoreId,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("distance")] double Distance);

public sealed record CircleguardSimilarity(
    [property: JsonPropertyName("comparisons")] IReadOnlyList<CircleguardMatch> Comparisons,
    [property: JsonPropertyName("threshold")] double Threshold,
    [property: JsonPropertyName("matched")] CircleguardMatch? Matched);

/// <summary>What the service found (its JSON, the parts TomYum uses).</summary>
public sealed record CircleguardVerdict(
    [property: JsonPropertyName("risk_score")] int RiskScore,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons,
    [property: JsonPropertyName("mods")] string Mods,
    [property: JsonPropertyName("unstable_rate")] double? UnstableRate,
    [property: JsonPropertyName("converted_unstable_rate")] double? ConvertedUnstableRate,
    [property: JsonPropertyName("frametime")] double? Frametime,
    [property: JsonPropertyName("snap_count")] int SnapCount,
    [property: JsonPropertyName("similarity")] CircleguardSimilarity? Similarity,
    [property: JsonPropertyName("note")] string? Note);

/// <summary>Talks to the lazer-circleguard service.</summary>
public sealed class CircleguardClient
{
    private readonly HttpClient _http;
    private readonly CircleguardOptions _options;

    public CircleguardClient(HttpClient http, CircleguardOptions options)
    {
        _http = http;
        _options = options;
    }

    /// <summary>The service's verdict on <paramref name="play"/>.</summary>
    public async Task<CircleguardVerdict> AnalyzeAsync(CircleguardPlay play, byte[]? beatmap, IReadOnlyList<CircleguardReference> references, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(play);
        ArgumentNullException.ThrowIfNull(references);
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(play.Data), "replay", "play.osr" },
        };
        if (play.FramesOnly)
        {
            form.Add(new StringContent(JsonSerializer.Serialize(new
            {
                frames_only = true,
                ruleset = play.Ruleset,
                mods = play.Mods,
                username = play.Username,
                max_combo = play.MaxCombo,
                count_300 = play.Count300,
                count_100 = play.Count100,
                count_50 = play.Count50,
                count_geki = play.CountGeki,
                count_katu = play.CountKatu,
                count_miss = play.CountMiss,
            })), "replay_meta");
        }
        if (beatmap is not null)
            form.Add(new ByteArrayContent(beatmap), "beatmap", "map.osu");
        foreach (var reference in references)
            form.Add(new ByteArrayContent(reference.Data), "reference", reference.ScoreId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (references.Count > 0)
        {
            form.Add(new StringContent(JsonSerializer.Serialize(references.Select(r => new
            {
                score_id = r.ScoreId,
                username = r.Username,
                mods = r.Mods,
                frames_only = r.FramesOnly,
            }))), "reference_meta");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Url.TrimEnd('/') + "/analyze") { Content = form };
        request.Headers.Add("X-Circleguard-Key", _options.Key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CircleguardVerdict>(cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("circleguard gave an empty answer.");
    }
}
