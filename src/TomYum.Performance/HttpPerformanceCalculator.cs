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
            request.MaxCombo, request.Passed, request.Lazer, request.LargeTickHits, request.SliderEndHits,
            CustomRate(request));

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);

            var body = await PostAsync(payload, cts.Token).ConfigureAwait(false);
            // A sidecar that doesn't say it applied the speed predates clockRate and paid the rate mod at its
            // usual speed: ask again with the legacy-bits stopgap, so a custom speed is never paid as 1.5x.
            if (payload.ClockRate is not null && body.ClockRate is null)
                body = await PostAsync(payload with { Mods = SidecarMods(request), ClockRate = null }, cts.Token).ConfigureAwait(false);

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

    private async Task<PpSidecarResponse> PostAsync(PpSidecarRequest payload, CancellationToken cancellationToken)
    {
        using var resp = await _http.PostAsJsonAsync(_url, payload, Json, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<PpSidecarResponse>(Json, cancellationToken).ConfigureAwait(false);

        if (body is null || double.IsNaN(body.Pp) || double.IsInfinity(body.Pp))
            throw new InvalidOperationException("sidecar returned no/invalid pp");
        return body;
    }

    private const int DoubleTime = 1 << 6;
    private const int HalfTime = 1 << 8;
    private const int Nightcore = 1 << 9;

    /// <summary>
    /// The speed a lazer play's rate mod ran at when it isn't the mod's usual one (DT/NC 1.5x, HT/DC 0.75x), for
    /// the sidecar's clockRate; null when there's no rate mod or it ran at its usual speed.
    /// </summary>
    public static double? CustomRate(PerformanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SpeedChange is not { } speed || !double.IsFinite(speed))
            return null;
        double usual = (request.Mods & (DoubleTime | Nightcore)) != 0 ? 1.5
            : (request.Mods & HalfTime) != 0 ? 0.75
            : double.NaN;
        return double.IsNaN(usual) || Math.Abs(speed - usual) < 1e-9 ? null : speed;
    }

    /// <summary>
    /// The stopgap for a sidecar without clockRate, which only knows the legacy bits, where DT/NC means 1.5x: a
    /// lazer play can set its own speed (DT at 1.01x), which would be paid as 1.5x. DT/NC below 1.5x is paid as no
    /// rate mod at all, so it can never be paid more than it was played.
    /// </summary>
    public static int SidecarMods(PerformanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SpeedChange is { } speed && speed < 1.5 && (request.Mods & (DoubleTime | Nightcore)) != 0)
            return request.Mods & ~(DoubleTime | Nightcore);
        return request.Mods;
    }
}

/// <summary>
/// The request a pp sidecar receives. <c>Mode</c> is the vanilla ruleset (0=osu!,1=taiko,2=catch,
/// 3=mania); Relax/Autopilot are conveyed via <c>Mods</c> (osu! legacy bitmask) so the sidecar
/// applies whatever relax/autopilot algorithm it implements. Hit counts follow osu! conventions.
/// <c>ClockRate</c> is a lazer rate mod's own speed (none: the mod's usual).
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
    [property: JsonPropertyName("sliderEndHits")] int? SliderEndHits,
    [property: JsonPropertyName("clockRate")] double? ClockRate = null);

/// <summary>
/// The response a pp sidecar returns. <c>StarRating</c> is optional. <c>ClockRate</c> is the speed it paid a
/// rate mod at, when the request's <c>clockRate</c> was applied (a sidecar without it leaves it out).
/// </summary>
public sealed record PpSidecarResponse(
    [property: JsonPropertyName("pp")] double Pp,
    [property: JsonPropertyName("starRating")] double? StarRating = null,
    [property: JsonPropertyName("clockRate")] double? ClockRate = null);
