using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace TomYum.Performance;

/// <summary>
/// An <see cref="IPerformanceCalculator"/> that delegates to an external HTTP sidecar (rosu-pp,
/// akatsuki-pp-rs, a fork of osu-tools, …). The sidecar contract is small and stable so any
/// implementation can satisfy it: POST <see cref="PpSidecarRequest"/> JSON, receive
/// <see cref="PpSidecarResponse"/>. On any failure it falls back to a configured backend (or
/// returns 0 pp) so score submission never breaks on a sidecar hiccup. See ADR-0010.
/// </summary>
public sealed class HttpPerformanceCalculator : IPerformanceCalculator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly TimeSpan _timeout;
    private readonly string _backendName;
    private readonly Func<PerformanceRequest, CancellationToken, Task<double>>? _fallback;
    private readonly ILogger _log;

    public HttpPerformanceCalculator(
        HttpClient http,
        string url,
        TimeSpan timeout,
        string backendName,
        Func<PerformanceRequest, CancellationToken, Task<double>>? fallback,
        ILogger log)
    {
        _http = http;
        _url = url;
        _timeout = timeout;
        _backendName = backendName;
        _fallback = fallback;
        _log = log;
    }

    public async Task<double> CalculateAsync(PerformanceRequest request, CancellationToken cancellationToken = default)
    {
        var payload = new PpSidecarRequest(
            request.BeatmapId,
            request.RulesetId,   // vanilla ruleset 0-3; the sidecar reads Relax/Autopilot from Mods
            request.Mods,
            request.Count300, request.Count100, request.Count50,
            request.CountGeki, request.CountKatu, request.CountMiss,
            request.MaxCombo, request.Passed, request.Lazer, request.LargeTickHits, request.SliderEndHits);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);

            using var resp = await _http.PostAsJsonAsync(_url, payload, Json, cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<PpSidecarResponse>(Json, cts.Token).ConfigureAwait(false);

            if (body is null || double.IsNaN(body.Pp) || double.IsInfinity(body.Pp))
                throw new InvalidOperationException("sidecar returned no/invalid pp");

            return body.Pp;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Timeout, transport error, non-2xx, or bad body — do not fail submission over pp.
            if (_fallback is not null)
            {
                _log.LogWarning(ex, "pp sidecar '{Backend}' ({Url}) failed; using fallback backend.", _backendName, _url);
                return await _fallback(request, cancellationToken).ConfigureAwait(false);
            }

            _log.LogWarning(ex, "pp sidecar '{Backend}' ({Url}) failed and has no fallback; recording 0 pp.", _backendName, _url);
            return 0.0;
        }
    }
}

/// <summary>
/// The request a pp sidecar receives. <c>Mode</c> is the vanilla ruleset (0=osu!,1=taiko,2=catch,
/// 3=mania); Relax/Autopilot are conveyed via <c>Mods</c> (osu! legacy bitmask) so the sidecar
/// applies whatever relax/autopilot algorithm it implements. Hit counts follow osu! conventions.
/// </summary>
public sealed record PpSidecarRequest(
    [property: JsonPropertyName("beatmapId")] int BeatmapId,
    [property: JsonPropertyName("mode")] int Mode,
    [property: JsonPropertyName("mods")] int Mods,
    [property: JsonPropertyName("n300")] int N300,
    [property: JsonPropertyName("n100")] int N100,
    [property: JsonPropertyName("n50")] int N50,
    [property: JsonPropertyName("nGeki")] int NGeki,
    [property: JsonPropertyName("nKatu")] int NKatu,
    [property: JsonPropertyName("nMiss")] int NMiss,
    [property: JsonPropertyName("maxCombo")] int MaxCombo,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("lazer")] bool Lazer,
    [property: JsonPropertyName("largeTickHits")] int? LargeTickHits,
    [property: JsonPropertyName("sliderEndHits")] int? SliderEndHits);

/// <summary>The response a pp sidecar returns. <c>StarRating</c> is optional.</summary>
public sealed record PpSidecarResponse(
    [property: JsonPropertyName("pp")] double Pp,
    [property: JsonPropertyName("starRating")] double? StarRating = null);
