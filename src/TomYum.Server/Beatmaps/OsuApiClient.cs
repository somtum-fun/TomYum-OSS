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

    /// <summary>
    /// Fetches every difficulty of a beatmap set. Empty when the set is unknown or the request
    /// fails — like <see cref="GetByMd5Async"/>, failures are logged rather than thrown.
    /// </summary>
    Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Up to 500 ranked/approved/qualified/loved difficulties with an approved date at or after
    /// <paramref name="since"/>, oldest first. Only the official API supports this (it needs an API
    /// key); empty otherwise or when the request fails.
    /// </summary>
    Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default);

    /// <summary>Whether <see cref="GetSinceAsync"/> is available (an API key is configured).</summary>
    bool SupportsSince { get; }

    /// <summary>
    /// Fetches one difficulty by its beatmap id, or <c>null</c> when unknown or the request fails
    /// (logged, like the other lookups).
    /// </summary>
    Task<Beatmap?> GetByIdAsync(int beatmapId, CancellationToken cancellationToken = default) => Task.FromResult<Beatmap?>(null);
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

        try
        {
            var beatmaps = await _http.GetFromJsonAsync<List<OsuApiBeatmap>>(Query($"h={WebUtility.UrlEncode(md5)}"), JsonOptions, cancellationToken).ConfigureAwait(false);

            // The keyless osu.direct mirror ignores the `h=` filter and returns EVERY difficulty in
            // the set — each with its own file_md5 — whereas keyed old.ppy.sh returns only the exact
            // match. Select the difficulty whose md5 actually equals the requested hash. Taking the
            // first entry blindly files every play on a non-first difficulty under the wrong beatmap
            // id (and then computes pp against the wrong map's difficulty — e.g. a 1★ convert scored
            // as a 4★ one). If nothing matches, treat the map as unsubmitted rather than guess.
            var match = beatmaps?.FirstOrDefault(b => string.Equals(b.FileMd5, md5, StringComparison.OrdinalIgnoreCase));
            if (match is null && beatmaps is { Count: > 0 })
                _logger.LogWarning("osu! API returned {Count} beatmap(s) for md5 {Md5} but none matched the hash; treating as unsubmitted.", beatmaps.Count, md5);

            return match is null ? null : Map(match);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException or System.Text.Json.JsonException
            && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "osu! API lookup failed for md5 {Md5}; treating as unsubmitted.", md5);
            OsuApiFailures.Report();
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default)
    {
        if (setId <= 0)
        {
            return [];
        }

        try
        {
            var beatmaps = await _http.GetFromJsonAsync<List<OsuApiBeatmap>>(Query($"s={setId}"), JsonOptions, cancellationToken).ConfigureAwait(false);

            // Keep only this set's difficulties, in case a mirror answers with more than asked for.
            return beatmaps?
                .Select(Map)
                .Where(b => b.SetId == setId && b.Id > 0 && !string.IsNullOrEmpty(b.Md5))
                .ToList() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException or System.Text.Json.JsonException
            && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "osu! API lookup failed for beatmap set {SetId}.", setId);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<Beatmap?> GetByIdAsync(int beatmapId, CancellationToken cancellationToken = default)
    {
        if (beatmapId <= 0)
        {
            return null;
        }

        try
        {
            var beatmaps = await _http.GetFromJsonAsync<List<OsuApiBeatmap>>(Query($"b={beatmapId}"), JsonOptions, cancellationToken).ConfigureAwait(false);

            // Like h=, a mirror may answer with the whole set: pick the asked-for difficulty.
            return beatmaps?.Select(Map).FirstOrDefault(b => b.Id == beatmapId && b.SetId > 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException or System.Text.Json.JsonException
            && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "osu! API lookup failed for beatmap {BeatmapId}.", beatmapId);
            return null;
        }
    }

    /// <inheritdoc />
    public bool SupportsSince => !string.IsNullOrEmpty(_options.ApiKey);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        if (!SupportsSince)
        {
            return [];
        }

        var filter = string.Create(CultureInfo.InvariantCulture,
            $"since={WebUtility.UrlEncode(since.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}&limit=500");
        try
        {
            var beatmaps = await _http.GetFromJsonAsync<List<OsuApiBeatmap>>(Query(filter), JsonOptions, cancellationToken).ConfigureAwait(false);
            return beatmaps?.Select(Map).Where(b => b.Id > 0 && b.SetId > 0).ToList() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException or System.Text.Json.JsonException
            && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "osu! API lookup failed for maps ranked since {Since}.", since);
            return [];
        }
    }

    /// <summary>
    /// Builds a <c>get_beatmaps</c> query. old.ppy.sh requires a key; osu.direct does not, so
    /// <c>k=</c> is only appended when one is configured.
    /// </summary>
    private string Query(string filter) => string.IsNullOrEmpty(_options.ApiKey)
        ? $"get_beatmaps?{filter}"
        : $"get_beatmaps?k={WebUtility.UrlEncode(_options.ApiKey)}&{filter}";

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
        OsuApiStatus = ParseInt(api.Approved),
        Mode = ParseInt(api.Mode),
        StarRating = ParseDouble(api.DifficultyRating),
        TotalLength = ParseInt(api.TotalLength),
        MaxCombo = ParseInt(api.MaxCombo),
        LastUpdate = ParseDate(api.LastUpdate),
        Details = new BeatmapDetails
        {
            CircleSize = ParseFloatOrNull(api.DiffSize),
            ApproachRate = ParseFloatOrNull(api.DiffApproach),
            OverallDifficulty = ParseFloatOrNull(api.DiffOverall),
            DrainRate = ParseFloatOrNull(api.DiffDrain),
            Bpm = ParseFloatOrNull(api.Bpm),
            HitLength = ParseIntOrNull(api.HitLength),
            CountCircles = ParseIntOrNull(api.CountNormal),
            CountSliders = ParseIntOrNull(api.CountSlider),
            CountSpinners = ParseIntOrNull(api.CountSpinner),
            // 0 isn't a real genre/language (osu.direct reports it for every map): leave unknown.
            GenreId = ParseIntOrNull(api.GenreId) is > 0 and var genre ? genre : null,
            LanguageId = ParseIntOrNull(api.LanguageId) is > 0 and var language ? language : null,
            Source = api.Source,
            Tags = api.Tags,
            Video = ParseIntOrNull(api.Video) is { } video ? video != 0 : null,
            Storyboard = ParseIntOrNull(api.Storyboard) is { } storyboard ? storyboard != 0 : null,
            ApprovedDate = string.IsNullOrEmpty(api.ApprovedDate) ? null : ParseDate(api.ApprovedDate),
            PackTags = ParsePackTags(api.Packs),
        },
    };

    /// <summary>Pack tags from an array (osu.direct) or a comma-separated string (old.ppy.sh); null when absent.</summary>
    private static List<string>? ParsePackTags(JsonElement packs)
    {
        IEnumerable<string?> raw = packs.ValueKind switch
        {
            JsonValueKind.Array => packs.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : null),
            JsonValueKind.String => (packs.GetString() ?? string.Empty).Split(','),
            _ => [],
        };
        var tags = raw
            .Select(t => t?.Trim() ?? string.Empty)
            .Where(t => t.Length is > 0 and <= 5) // osu-web's tag column is varchar(5)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return packs.ValueKind is JsonValueKind.Array or JsonValueKind.String ? tags : null;
    }

    private static int ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static int? ParseIntOrNull(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static double ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0.0;

    private static float? ParseFloatOrNull(string? value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static DateTimeOffset ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result)
            ? result
            : DateTimeOffset.MinValue;
}
