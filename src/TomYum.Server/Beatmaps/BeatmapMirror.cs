using System.Globalization;
using System.Net;
using System.Text.Json;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;

namespace TomYum.Server.Beatmaps;

/// <summary>Settings for the beatmap mirror asked before osu!'s API (section <c>Mirror</c>).</summary>
public sealed class MirrorOptions
{
    public const string SectionName = "Mirror";

    /// <summary>Whether map lookups ask the mirror first (default yes).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A mirror with osu!'s v2 answers at <c>api/v2/b|s|md5/…</c> (catboy.best's API).</summary>
    public string Url { get; set; } = "https://catboy.best/";

    /// <summary>How many requests a minute this server makes to it.</summary>
    public int RequestsPerMinute { get; set; } = 120;

    /// <summary>
    /// How the server introduces itself; defaults to naming its website (catboy.best refuses
    /// anonymous clients, and ones whose link doesn't lead anywhere public).
    /// </summary>
    public string? UserAgent { get; set; }
}

/// <summary>
/// Map lookups from a beatmap mirror (catboy.best): the same data as osu!'s API v2, without using this
/// server's osu! API allowance, so imports (every map lazer asks about), stable's unknown maps and
/// map details mostly never reach osu!. Anything it doesn't know or can't answer right now comes back
/// null (or empty), and callers ask osu! instead (<see cref="MirrorFirstOsuApiClient"/>).
/// </summary>
public sealed class BeatmapMirror
{
    private readonly HttpClient _http;
    private readonly MirrorOptions _options;
    private readonly OsuApiBudget _pace;
    private readonly ILogger<BeatmapMirror> _logger;

    public BeatmapMirror(HttpClient http, MirrorOptions options, [FromKeyedServices(PaceKey)] OsuApiBudget pace, ILogger<BeatmapMirror> logger)
    {
        _http = http;
        _options = options;
        _pace = pace;
        _logger = logger;
    }

    /// <summary>The DI key of the mirror's own pace.</summary>
    public const string PaceKey = "mirror";

    /// <summary>The server's introduction: <see cref="MirrorOptions.UserAgent"/>, else one naming <paramref name="site"/>.</summary>
    public static string UserAgentFor(MirrorOptions options, string site)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.IsNullOrWhiteSpace(options.UserAgent) ? $"TomYum/0.1 (osu! private server; +{site})" : options.UserAgent;
    }

    public bool IsEnabled => _options.Enabled;

    public async Task<Beatmap?> GetByIdAsync(int beatmapId, CancellationToken cancellationToken = default)
    {
        if (beatmapId <= 0)
            return null;
        using var json = await GetAsync($"api/v2/b/{beatmapId}", cancellationToken).ConfigureAwait(false);
        return json is null ? null : ParseBeatmapWithSet(json.RootElement);
    }

    public async Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(md5) || md5.Length != 32 || !md5.All(char.IsAsciiHexDigit))
            return null;
        using var json = await GetAsync($"api/v2/md5/{md5.ToLowerInvariant()}", cancellationToken).ConfigureAwait(false);
        return json is null ? null : ParseBeatmapWithSet(json.RootElement) is { } map && string.Equals(map.Md5, md5, StringComparison.OrdinalIgnoreCase) ? map : null;
    }

    public async Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default)
    {
        if (setId <= 0)
            return [];
        using var json = await GetAsync($"api/v2/s/{setId}", cancellationToken).ConfigureAwait(false);
        return json is null ? [] : ParseSet(json.RootElement);
    }

    /// <summary>The set as osu!'s API v2 describes it (nominators, featured artist track, …); null when unknown.</summary>
    public Task<JsonDocument?> GetSetJsonAsync(int setId, CancellationToken cancellationToken = default) =>
        setId <= 0 ? Task.FromResult<JsonDocument?>(null) : GetAsync($"api/v2/s/{setId}", cancellationToken);

    /// <summary>Every difficulty of a v2 beatmapset.</summary>
    internal static IReadOnlyList<Beatmap> ParseSet(JsonElement set) =>
        set.TryGetProperty("beatmaps", out var maps) && maps.ValueKind == JsonValueKind.Array
            ? [.. maps.EnumerateArray().Select(map => Parse(map, set)).Where(b => b.Id > 0 && b.SetId > 0)]
            : [];

    /// <summary>A v2 beatmap carrying its set (<c>set</c> or <c>beatmapset</c>).</summary>
    internal static Beatmap? ParseBeatmapWithSet(JsonElement map)
    {
        var set = map.TryGetProperty("set", out var s) && s.ValueKind == JsonValueKind.Object ? s
            : map.TryGetProperty("beatmapset", out var b) && b.ValueKind == JsonValueKind.Object ? b
            : default;
        var beatmap = Parse(map, set);
        return beatmap.Id > 0 && beatmap.SetId > 0 ? beatmap : null;
    }

    /// <summary>A v2 beatmap as this server's <see cref="Beatmap"/> (what the v1 API gave).</summary>
    internal static Beatmap Parse(JsonElement map, JsonElement set)
    {
        var ranked = Int(map, "ranked") ?? Int(set, "ranked") ?? 0;
        return new Beatmap
        {
            Md5 = String(map, "checksum") ?? string.Empty,
            Id = Int(map, "id") ?? 0,
            SetId = Int(map, "beatmapset_id") ?? Int(set, "id") ?? 0,
            Artist = String(set, "artist") ?? string.Empty,
            Title = String(set, "title") ?? string.Empty,
            Version = String(map, "version") ?? string.Empty,
            Creator = String(set, "creator") ?? string.Empty,
            Status = RankedStatusExtensions.FromOsuApi(ranked),
            OsuApiStatus = ranked,
            Mode = Int(map, "mode_int") ?? 0,
            StarRating = Double(map, "difficulty_rating") ?? 0,
            TotalLength = Int(map, "total_length") ?? 0,
            MaxCombo = Int(map, "max_combo") ?? 0,
            LastUpdate = Date(map, "last_updated") ?? Date(set, "last_updated") ?? DateTimeOffset.MinValue,
            Details = new BeatmapDetails
            {
                CircleSize = Float(map, "cs"),
                ApproachRate = Float(map, "ar"),
                OverallDifficulty = Float(map, "accuracy"),
                DrainRate = Float(map, "drain"),
                Bpm = Float(map, "bpm"),
                HitLength = Int(map, "hit_length"),
                CountCircles = Int(map, "count_circles"),
                CountSliders = Int(map, "count_sliders"),
                CountSpinners = Int(map, "count_spinners"),
                GenreId = Int(set, "genre_id") is > 0 and var genre ? genre : null,
                LanguageId = Int(set, "language_id") is > 0 and var language ? language : null,
                Source = String(set, "source"),
                Tags = String(set, "tags"),
                Video = Bool(set, "video"),
                Storyboard = Bool(set, "storyboard"),
                ApprovedDate = Date(set, "ranked_date"),
                PackTags = set.ValueKind == JsonValueKind.Object && set.TryGetProperty("pack_tags", out var packs) && packs.ValueKind == JsonValueKind.Array
                    ? [.. packs.EnumerateArray().Select(p => p.GetString()).OfType<string>()]
                    : null,
            },
        };
    }

    private async Task<JsonDocument?> GetAsync(string path, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return null;
        try
        {
            await _pace.WaitAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Beatmap mirror: {Status} for {Path}; asking osu! instead.", (int)response.StatusCode, path);
                return null;
            }
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "Beatmap mirror: {Path} failed; asking osu! instead.", path);
            return null;
        }
    }

    private static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static string? String(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static int? Int(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;

    private static double? Double(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;

    private static float? Float(JsonElement element, string name) => Double(element, name) is { } number ? (float)number : null;

    private static bool? Bool(JsonElement element, string name) =>
        Property(element, name) is { } value && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static DateTimeOffset? Date(JsonElement element, string name) =>
        String(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
}

/// <summary>
/// The osu! API client the server uses: the beatmap mirror first, osu!'s own API (through the shared
/// <see cref="OsuApiBudget"/>) for what the mirror doesn't know. Listing newly ranked maps stays with
/// osu! (the mirror has no such listing).
/// </summary>
public sealed class MirrorFirstOsuApiClient : IOsuApiClient
{
    private readonly BeatmapMirror _mirror;
    private readonly OsuApiClient _osu;

    public MirrorFirstOsuApiClient(BeatmapMirror mirror, OsuApiClient osu)
    {
        _mirror = mirror;
        _osu = osu;
    }

    public async Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) =>
        await _mirror.GetByMd5Async(md5, cancellationToken).ConfigureAwait(false)
        ?? await _osu.GetByMd5Async(md5, cancellationToken).ConfigureAwait(false);

    public async Task<Beatmap?> GetByIdAsync(int beatmapId, CancellationToken cancellationToken = default) =>
        await _mirror.GetByIdAsync(beatmapId, cancellationToken).ConfigureAwait(false)
        ?? await _osu.GetByIdAsync(beatmapId, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) =>
        await _mirror.GetSetAsync(setId, cancellationToken).ConfigureAwait(false) is { Count: > 0 } fromMirror
            ? fromMirror
            : await _osu.GetSetAsync(setId, cancellationToken).ConfigureAwait(false);

    public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
        _osu.GetSinceAsync(since, cancellationToken);

    public bool SupportsSince => _osu.SupportsSince;
}
