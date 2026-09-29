using TomYum.Persistence.OsuWeb;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TomYum.Server.Beatmaps;

/// <summary>An osu! user who nominated a set (they have no account on this server).</summary>
public sealed record OsuNominator(int OsuUserId, string Username, IReadOnlyList<string>? Rulesets);

/// <summary>What the website shows about a set that the v1 API doesn't have.</summary>
/// <param name="DescriptionBbcode">The description as its author wrote it (BBCode); null when the set has none.</param>
/// <param name="Nominators">The current nominators (empty for sets nobody nominated, e.g. loved or graveyard).</param>
/// <param name="TrackId">The featured artist track the set is of (osu!'s track id), or null when it isn't a featured artist song.</param>
public sealed record BeatmapsetExtras(string? DescriptionBbcode, IReadOnlyList<OsuNominator> Nominators, int? TrackId = null);

/// <summary>
/// The osu! API v2, with the client credentials of an OAuth application (<c>Osu:ClientId</c> /
/// <c>Osu:ClientSecret</c>, public scope). Used for what the v1 API lacks: beatmapset descriptions
/// (the first post of the set's forum thread, as BBCode), current nominators, and whether the song is
/// a featured artist's.
/// </summary>
public sealed class OsuApiV2Client
{
    /// <summary>osu!'s description posts start with a generated header, then this line.</summary>
    private const string DescriptionSeparator = "---------------";

    private readonly HttpClient _http;
    private readonly BeatmapMirror? _mirror;
    private readonly OsuApiOptions _options;
    private readonly ILogger<OsuApiV2Client> _logger;
    // Typed clients are created per use: the token is shared by the process.
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static string? _token;
    private static DateTimeOffset _tokenExpires;

    public OsuApiV2Client(HttpClient http, OsuApiOptions options, ILogger<OsuApiV2Client> logger, BeatmapMirror? mirror = null)
    {
        _mirror = mirror;
        _http = http;
        _options = options;
        _logger = logger;
    }

    /// <summary>Whether an OAuth application is configured.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ClientId) && !string.IsNullOrWhiteSpace(_options.ClientSecret);

    /// <summary>The set's description and nominators; null when not configured, unknown, or the request fails (logged).</summary>
    public async Task<BeatmapsetExtras?> GetBeatmapsetExtrasAsync(int setId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || setId <= 0)
            return null;

        try
        {
            // The mirror has osu!'s answer for the set; osu! is asked only when it doesn't.
            using var set = (_mirror is { IsEnabled: true } ? await _mirror.GetSetJsonAsync(setId, cancellationToken).ConfigureAwait(false) : null)
                ?? await GetJsonAsync($"api/v2/beatmapsets/{setId}", cancellationToken).ConfigureAwait(false);
            if (set is null)
                return null;

            var (nominators, topicId) = ParseBeatmapset(set.RootElement);
            int? trackId = set.RootElement.TryGetProperty("track_id", out var track) && track.ValueKind == JsonValueKind.Number
                && track.TryGetInt32(out var id) && id > 0 ? id : null;
            string? description = null;
            if (topicId is { } topic)
            {
                using var thread = await GetJsonAsync($"api/v2/forums/topics/{topic}?limit=1&sort=id_asc", cancellationToken).ConfigureAwait(false);
                if (thread is not null)
                    description = DescriptionFromTopic(thread.RootElement);
            }
            return new BeatmapsetExtras(description, nominators, trackId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "osu! API v2 lookup failed for beatmap set {SetId}.", setId);
            return null;
        }
    }

    /// <summary>osu!'s beatmap tags (<c>/api/v2/tags</c>); null when osu! can't be asked.</summary>
    public async Task<IReadOnlyList<BeatmapTagDefinition>?> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return null;
        try
        {
            using var json = await GetJsonAsync("api/v2/tags", cancellationToken).ConfigureAwait(false);
            return json is null ? null : ParseTags(json.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "osu! API v2: couldn't get the beatmap tags.");
            return null;
        }
    }

    /// <summary>The tags in a <c>{"tags":[{id, name, ruleset_id, description}]}</c> answer.</summary>
    internal static IReadOnlyList<BeatmapTagDefinition> ParseTags(JsonElement root)
    {
        var tags = new List<BeatmapTagDefinition>();
        if (!root.TryGetProperty("tags", out var list) || list.ValueKind != JsonValueKind.Array)
            return tags;
        foreach (var tag in list.EnumerateArray())
        {
            if (!tag.TryGetProperty("id", out var id) || !id.TryGetInt64(out var tagId)
                || !tag.TryGetProperty("name", out var name) || name.GetString() is not { Length: > 0 } tagName)
                continue;
            int? ruleset = tag.TryGetProperty("ruleset_id", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var rulesetId) ? rulesetId : null;
            var description = tag.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? string.Empty : string.Empty;
            tags.Add(new(tagId, tagName, ruleset, description));
        }
        return tags;
    }

    /// <summary>Current nominators (named from <c>related_users</c>) and the description thread's id.</summary>
    internal static (IReadOnlyList<OsuNominator> Nominators, int? TopicId) ParseBeatmapset(JsonElement set)
    {
        var names = new Dictionary<int, string>();
        if (set.TryGetProperty("related_users", out var users) && users.ValueKind == JsonValueKind.Array)
        {
            foreach (var user in users.EnumerateArray())
            {
                if (user.TryGetProperty("id", out var id) && id.TryGetInt32(out var userId)
                    && user.TryGetProperty("username", out var name) && name.GetString() is { } username)
                    names[userId] = username;
            }
        }

        var nominators = new List<OsuNominator>();
        if (set.TryGetProperty("current_nominations", out var nominations) && nominations.ValueKind == JsonValueKind.Array)
        {
            foreach (var nomination in nominations.EnumerateArray())
            {
                if (!nomination.TryGetProperty("user_id", out var id) || !id.TryGetInt32(out var userId)
                    || (nomination.TryGetProperty("reset", out var reset) && reset.ValueKind == JsonValueKind.True))
                    continue;

                IReadOnlyList<string>? rulesets = nomination.TryGetProperty("rulesets", out var r) && r.ValueKind == JsonValueKind.Array
                    ? r.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList()
                    : null;
                nominators.Add(new OsuNominator(userId, names.GetValueOrDefault(userId, $"#{userId}"), rulesets));
            }
        }

        // "https://osu.ppy.sh/community/forums/topics/123"
        int? topicId = set.TryGetProperty("legacy_thread_url", out var url) && url.GetString() is { } threadUrl
            && int.TryParse(threadUrl.TrimEnd('/').Split('/')[^1], out var parsed) && parsed > 0
            ? parsed
            : null;

        return (nominators.DistinctBy(n => n.OsuUserId).ToList(), topicId);
    }

    /// <summary>
    /// The description from the thread's first post, as osu-web cuts it: what follows the generated
    /// header's separator line. Null without a separator or text.
    /// </summary>
    internal static string? DescriptionFromTopic(JsonElement topic)
    {
        if (!topic.TryGetProperty("posts", out var posts) || posts.ValueKind != JsonValueKind.Array || posts.GetArrayLength() == 0)
            return null;

        var first = posts.EnumerateArray().First();
        if (!first.TryGetProperty("body", out var body) || !body.TryGetProperty("raw", out var raw) || raw.GetString() is not { } text)
            return null;

        var at = text.IndexOf(DescriptionSeparator, StringComparison.Ordinal);
        if (at < 0)
            return null;
        var description = text[(at + DescriptionSeparator.Length)..].Trim();
        return description.Length == 0 ? null : description;
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(cancellationToken).ConfigureAwait(false));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        await TokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpires)
                return _token;

            using var response = await _http.PostAsync("oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId!,
                ["client_secret"] = _options.ClientSecret!,
                ["grant_type"] = "client_credentials",
                ["scope"] = "public",
            }), cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("osu! returned no token.");

            _token = token.AccessToken;
            _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60));
            return _token;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private sealed record TokenResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string AccessToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int ExpiresIn);
}
