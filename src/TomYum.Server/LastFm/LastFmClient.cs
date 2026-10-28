using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TomYum.Server.LastFm;

/// <summary>Last.fm settings (<c>TOMYUM_LASTFM__APIKEY</c>, <c>TOMYUM_LASTFM__APISECRET</c>, from last.fm/api/account/create).</summary>
public sealed class LastFmOptions
{
    public const string SectionName = "LastFm";

    public string ApiKey { get; set; } = string.Empty;

    public string ApiSecret { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);
}

/// <summary>A song as Last.fm gets it.</summary>
/// <param name="Artist">The artist.</param>
/// <param name="Title">The song's title.</param>
/// <param name="DurationSeconds">How long it played (the map's length at the play's speed).</param>
public sealed record LastFmTrack(string Artist, string Title, int DurationSeconds);

/// <summary>What someone is listening to right now, as Last.fm shows it (from osu! or anything else that scrobbles).</summary>
public sealed record LastFmListening(string Artist, string Title, string? Url, string? Image = null);

/// <summary>A track in someone's Last.fm history (from osu! or anything else that scrobbles).</summary>
/// <param name="Artist">The artist.</param>
/// <param name="Title">The song's title.</param>
/// <param name="Url">Its page on Last.fm.</param>
/// <param name="At">When it was scrobbled (when it started); null while it's playing.</param>
/// <param name="NowPlaying">Whether it's playing right now.</param>
/// <param name="Image">Its cover art (the biggest Last.fm has), or null.</param>
public sealed record LastFmRecentTrack(string Artist, string Title, string? Url, DateTimeOffset? At, bool NowPlaying, string? Image = null);

/// <summary>An error Last.fm answered with (https://www.last.fm/api/errorcodes).</summary>
public sealed class LastFmException : Exception
{
    public LastFmException()
    {
    }

    public LastFmException(string message)
        : base(message)
    {
    }

    public LastFmException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public LastFmException(int code, string message)
        : base(message)
    {
        Code = code;
    }

    public int Code { get; }

    /// <summary>The player took the server's access away (or the key went bad): their link is useless.</summary>
    public bool SessionInvalid => Code is 9;

    /// <summary>Last.fm is having trouble: try again later.</summary>
    public bool Temporary => Code is 11 or 16 or 29;
}

/// <summary>Last.fm's API (https://www.last.fm/api): connecting accounts, "now playing", scrobbles.</summary>
public sealed class LastFmClient
{
    public const string ApiUrl = "https://ws.audioscrobbler.com/2.0/";

    private readonly HttpClient _http;
    private readonly LastFmOptions _options;

    public LastFmClient(HttpClient http, LastFmOptions options)
    {
        _http = http;
        _options = options;
    }

    /// <summary>Where a player approves the server; Last.fm sends them back to <paramref name="callback"/> with a token.</summary>
    public string AuthUrl(string callback) =>
        $"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(_options.ApiKey)}&cb={Uri.EscapeDataString(callback)}";

    /// <summary>Trades an approved token for the player's Last.fm name and session key.</summary>
    public async Task<(string Username, string SessionKey)> GetSessionAsync(string token, CancellationToken cancellationToken = default)
    {
        using var answer = await CallAsync(new Dictionary<string, string>
        {
            ["method"] = "auth.getSession",
            ["token"] = token,
        }, cancellationToken).ConfigureAwait(false);
        var session = answer.RootElement.GetProperty("session");
        return (session.GetProperty("name").GetString() ?? string.Empty, session.GetProperty("key").GetString() ?? string.Empty);
    }

    public async Task NowPlayingAsync(string sessionKey, LastFmTrack track, CancellationToken cancellationToken = default)
    {
        var call = Track("track.updateNowPlaying", sessionKey, track);
        using var _ = await CallAsync(call, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Scrobbles <paramref name="track"/>. Last.fm takes some calls and still drops the scrobble (a filtered artist
    /// or title, "Various Artists" say): its reason then, else null.
    /// </summary>
    public async Task<string?> ScrobbleAsync(string sessionKey, LastFmTrack track, DateTimeOffset startedAt, CancellationToken cancellationToken = default)
    {
        var call = Track("track.scrobble", sessionKey, track);
        call["timestamp"] = startedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        call["chosenByUser"] = "1";
        using var answer = await CallAsync(call, cancellationToken).ConfigureAwait(false);
        return IgnoredReason(answer.RootElement);
    }

    /// <summary>Why Last.fm dropped a scrobble it was sent (scrobbles.scrobble.ignoredMessage), or null when it kept it.</summary>
    public static string? IgnoredReason(JsonElement answer)
    {
        if (!answer.TryGetProperty("scrobbles", out var scrobbles)
            || !scrobbles.TryGetProperty("@attr", out var attr)
            || !attr.TryGetProperty("ignored", out var ignored)
            || (ignored.ValueKind == JsonValueKind.Number ? ignored.GetInt32() : int.TryParse(ignored.GetString(), out var n) ? n : 0) == 0)
        {
            return null;
        }
        var scrobble = scrobbles.TryGetProperty("scrobble", out var one) ? one : default;
        if (scrobble.ValueKind == JsonValueKind.Array)
            scrobble = scrobble.EnumerateArray().FirstOrDefault();
        return scrobble.ValueKind == JsonValueKind.Object && scrobble.TryGetProperty("ignoredMessage", out var message)
               && message.TryGetProperty("#text", out var text) && text.GetString() is { Length: > 0 } reason
            ? reason
            : "ignored by Last.fm";
    }

    /// <summary>What Last.fm says <paramref name="username"/> listened to lately, newest first (the one playing now first).</summary>
    public async Task<IReadOnlyList<LastFmRecentTrack>> RecentAsync(string username, int limit, CancellationToken cancellationToken = default)
    {
        using var answer = await CallAsync(new Dictionary<string, string>
        {
            ["method"] = "user.getRecentTracks",
            ["user"] = username,
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
        }, cancellationToken, sign: false).ConfigureAwait(false);
        return Recent(answer.RootElement);
    }

    /// <summary>What Last.fm says <paramref name="username"/> is listening to now, or null.</summary>
    public async Task<LastFmListening?> ListeningAsync(string username, CancellationToken cancellationToken = default) =>
        Listening(await RecentAsync(username, 1, cancellationToken).ConfigureAwait(false));

    /// <summary>The track playing now among <paramref name="recent"/>, or null.</summary>
    public static LastFmListening? Listening(IEnumerable<LastFmRecentTrack> recent) =>
        recent.FirstOrDefault(t => t.NowPlaying) is { } now ? new LastFmListening(now.Artist, now.Title, now.Url, now.Image) : null;

    /// <summary>The tracks of a <c>user.getRecentTracks</c> answer.</summary>
    public static IReadOnlyList<LastFmRecentTrack> Recent(JsonElement answer)
    {
        if (!answer.TryGetProperty("recenttracks", out var recent) || !recent.TryGetProperty("track", out var tracks))
            return [];

        var list = tracks.ValueKind switch
        {
            JsonValueKind.Array => tracks.EnumerateArray().ToList(),
            JsonValueKind.Object => [tracks],
            _ => [],
        };
        var result = new List<LastFmRecentTrack>();
        foreach (var track in list)
        {
            var artist = track.TryGetProperty("artist", out var a)
                ? a.ValueKind == JsonValueKind.Object ? (a.TryGetProperty("#text", out var text) ? text.GetString() : null) : a.GetString()
                : null;
            var title = track.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(title))
                continue;
            var nowPlaying = track.TryGetProperty("@attr", out var attributes) && attributes.TryGetProperty("nowplaying", out var now) && now.GetString() == "true";
            DateTimeOffset? at = track.TryGetProperty("date", out var date) && date.TryGetProperty("uts", out var uts)
                && long.TryParse(uts.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
            result.Add(new LastFmRecentTrack(artist, title, track.TryGetProperty("url", out var url) ? url.GetString() : null, at, nowPlaying, Image(track)));
        }
        return result;
    }

    /// <summary>Last.fm's grey star, which it gives for tracks without art.</summary>
    private const string NoArt = "2a96cbd8b46e442fc41c2b86b821562f";

    /// <summary>A track's biggest cover art, or null (none, or only Last.fm's placeholder).</summary>
    private static string? Image(JsonElement track)
    {
        if (!track.TryGetProperty("image", out var images) || images.ValueKind != JsonValueKind.Array)
            return null;
        string? best = null;
        foreach (var image in images.EnumerateArray())
        {
            // Listed small to big: the last one with an address wins.
            if (image.TryGetProperty("#text", out var text) && text.GetString() is { Length: > 0 } address)
                best = address;
        }
        return best is null || best.Contains(NoArt, StringComparison.Ordinal) ? null : best;
    }

    /// <summary>
    /// Last.fm's call signature: every parameter (but <c>format</c> and <c>callback</c>) as name and
    /// value, sorted by name, then the secret, MD5 in hex.
    /// </summary>
    public static string Sign(IEnumerable<KeyValuePair<string, string>> parameters, string secret)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var text = new StringBuilder();
        foreach (var (name, value) in parameters.Where(p => p.Key is not ("format" or "callback")).OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append(name).Append(value);
        text.Append(secret);
#pragma warning disable CA5351 // Last.fm's protocol signs with MD5
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text.ToString())));
#pragma warning restore CA5351
    }

    private static Dictionary<string, string> Track(string method, string sessionKey, LastFmTrack track) => new()
    {
        ["method"] = method,
        ["sk"] = sessionKey,
        ["artist"] = track.Artist,
        ["track"] = track.Title,
        ["duration"] = track.DurationSeconds.ToString(CultureInfo.InvariantCulture),
    };

    private async Task<JsonDocument> CallAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken, bool sign = true)
    {
        parameters["api_key"] = _options.ApiKey;
        if (sign)
            parameters["api_sig"] = Sign(parameters, _options.ApiSecret);
        parameters["format"] = "json";

        using var content = new FormUrlEncodedContent(parameters);
        using var response = await _http.PostAsync(new Uri(ApiUrl), content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        JsonDocument answer;
        try
        {
            answer = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException($"Last.fm answered {(int)response.StatusCode} with something that isn't JSON.", ex);
        }

        if (answer.RootElement.ValueKind == JsonValueKind.Object && answer.RootElement.TryGetProperty("error", out var code))
        {
            var number = code.TryGetInt32(out var value) ? value : 0;
            var message = answer.RootElement.TryGetProperty("message", out var text) ? text.GetString() : null;
            answer.Dispose();
            throw new LastFmException(number, message ?? "Last.fm refused the call.");
        }
        return answer;
    }
}
