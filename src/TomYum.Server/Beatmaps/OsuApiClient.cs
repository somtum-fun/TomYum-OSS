using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// Resolves <see cref="Beatmap"/> metadata from the osu! v1 API (<c>get_beatmaps</c>). Mirrors
/// bancho.py: when an API key is configured the request goes to <c>old.ppy.sh</c>; with no key it
/// falls back to the keyless <c>osu.direct</c> mirror so a fresh deployment still resolves maps.
/// </summary>
public interface IOsuApiClient
{
    /// <summary>
    /// Fetches a beatmap by its <c>.osu</c> MD5 hash, or <c>null</c> when the API does not know it
    /// (an unsubmitted map). Network/transport failures also yield <c>null</c> — the caller treats
    /// an unresolved map as not-submitted rather than throwing on the login/getscores hot path.
    /// </summary>
    Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="HttpClient"/>-backed <see cref="IOsuApiClient"/>. The base address and key come from
/// configuration (<c>Osu:ApiKey</c> / <c>OSU_API_KEY</c>); see <c>OsuApiOptions</c>.
/// </summary>
public sealed class OsuApiClient : IOsuApiClient
{
    private readonly HttpClient _http;
    private readonly OsuApiOptions _options;
    private readonly ILogger<OsuApiClient> _logger;

    /// <summary>
    /// JSON options accepting both osu!-API flavours: <c>old.ppy.sh</c> returns every field as a
    /// quoted string, while the <c>osu.direct</c> mirror returns numeric fields as bare numbers.
    /// <see cref="FlexibleStringConverter"/> normalises both to string so deserialisation never throws.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new FlexibleStringConverter() },
    };

    /// <summary>Creates the client over a configured <see cref="HttpClient"/> and options.</summary>
    public OsuApiClient(HttpClient http, OsuApiOptions options, ILogger<OsuApiClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(md5))
        {
            return null;
        }

        // old.ppy.sh requires a key; osu.direct does not. Only append k= when we have one.
        var query = string.IsNullOrEmpty(_options.ApiKey)
            ? $"get_beatmaps?h={WebUtility.UrlEncode(md5)}"
            : $"get_beatmaps?k={WebUtility.UrlEncode(_options.ApiKey)}&h={WebUtility.UrlEncode(md5)}";

        try
        {
            var beatmaps = await _http.GetFromJsonAsync<List<OsuApiBeatmap>>(query, JsonOptions, cancellationToken).ConfigureAwait(false);
            var match = beatmaps?.FirstOrDefault();
            return match is null ? null : Map(match);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "osu! API lookup failed for md5 {Md5}; treating as unsubmitted.", md5);
            return null;
        }
    }

    /// <summary>Projects an osu! v1 API element onto a domain <see cref="Beatmap"/>.</summary>
    private static Beatmap Map(OsuApiBeatmap api) => new()
    {
        Md5 = api.FileMd5 ?? string.Empty,
        Id = ParseInt(api.BeatmapId),
        SetId = ParseInt(api.BeatmapsetId),
        Artist = api.Artist ?? string.Empty,
        Title = api.Title ?? string.Empty,
        Version = api.Version ?? string.Empty,
        Creator = api.Creator ?? string.Empty,
        Status = RankedStatusExtensions.FromOsuApi(ParseInt(api.Approved)),
        Mode = ParseInt(api.Mode),
        StarRating = ParseDouble(api.DifficultyRating),
        TotalLength = ParseInt(api.TotalLength),
        MaxCombo = ParseInt(api.MaxCombo),
        LastUpdate = ParseDate(api.LastUpdate),
    };

    private static int ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static double ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0.0;

    private static DateTimeOffset ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result)
            ? result
            : DateTimeOffset.MinValue;
}
