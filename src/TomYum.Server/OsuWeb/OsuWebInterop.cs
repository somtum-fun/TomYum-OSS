using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TomYum.Server.Beatmaps;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// Calls osu-web's InterOp API (<c>/_lio/*</c>), the endpoints osu!'s own game server uses to tell
/// the website about things that happen in-game. Requests are signed as osu-web's
/// <c>LegacyInterOpAuth</c> expects: <c>?timestamp=</c> (unix seconds, 5-minute window) and header
/// <c>X-LIO-Signature</c> = hex HMAC-SHA1 of the full URL keyed with <c>SHARED_INTEROP_SECRET</c>.
/// </summary>
public sealed class OsuWebInterop
{
    private readonly HttpClient _http;
    private readonly OsuWebInteropOptions _options;
    private readonly TimeProvider _time;

    public OsuWebInterop(HttpClient http, OsuWebInteropOptions options, TimeProvider? time = null)
    {
        _http = http;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>
    /// Unlocks a medal the way osu! does (<c>POST /_lio/user-achievement/{user}/{medal}[/{beatmap}]</c>):
    /// osu-web records it, adds the "unlocked a medal" activity entry, bumps the medal's count and
    /// notifies the player. Returns false when it was already unlocked.
    /// </summary>
    public async Task<bool> UnlockMedalAsync(int userId, int medalId, int? beatmapId, CancellationToken cancellationToken = default)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"/_lio/user-achievement/{userId}/{medalId}");
        if (beatmapId is > 0)
            path += string.Create(CultureInfo.InvariantCulture, $"/{beatmapId}");

        using var response = await PostAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            return false; // "user already unlocked the specified achievement"
        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// The "achieved rank #N on …" activity entry, as osu! writes it
    /// (<c>POST /_lio/users/{user}/{beatmap}/{ruleset}/rank-achieved</c>); <paramref name="grade"/> is the
    /// score's grade (<c>X</c>, <c>SH</c>, <c>A</c>, …), <paramref name="stable"/> whether it's a stable score.
    /// </summary>
    public async Task RankAchievedAsync(int userId, int beatmapId, int ruleset, int position, string grade, bool stable, CancellationToken cancellationToken = default)
    {
        var path = string.Create(CultureInfo.InvariantCulture,
            $"/_lio/users/{userId}/{beatmapId}/{ruleset}/rank-achieved?position_after={position}&rank={Uri.EscapeDataString(grade)}&legacy_score_event={(stable ? 1 : 0)}");
        using var response = await PostAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>The "has lost first place on …" activity entry (<c>POST /_lio/users/{user}/{beatmap}/{ruleset}/first-place-lost</c>).</summary>
    public async Task FirstPlaceLostAsync(int userId, int beatmapId, int ruleset, bool stable, CancellationToken cancellationToken = default)
    {
        var path = string.Create(CultureInfo.InvariantCulture,
            $"/_lio/users/{userId}/{beatmapId}/{ruleset}/first-place-lost?legacy_score_event={(stable ? 1 : 0)}");
        using var response = await PostAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Puts a user into osu-web's search index (<c>POST /_lio/user-index/{user}</c>).</summary>
    public async Task IndexUserAsync(int userId, CancellationToken cancellationToken = default)
    {
        using var response = await PostAsync(string.Create(CultureInfo.InvariantCulture, $"/_lio/user-index/{userId}"), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Re-indexes a beatmap set in osu-web's search, so the beatmap listing shows it — the database
    /// mirror alone doesn't reach Elasticsearch. Uses the fork's
    /// <c>/_lio/tomyum/index-beatmapset-document/{set}</c>: upstream's <c>/_lio/index-beatmapset</c>
    /// first regenerates covers by downloading the .osz through the download mirror, which fails
    /// (500) whenever osu-web can't reach it, and then never indexes. Falls back to upstream's on 404.
    /// </summary>
    public async Task IndexBeatmapsetAsync(int beatmapsetId, CancellationToken cancellationToken = default)
    {
        using var response = await PostAsync(
            string.Create(CultureInfo.InvariantCulture, $"/_lio/tomyum/index-beatmapset-document/{beatmapsetId}"), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
            return;
        }

        using var fallback = await PostAsync(string.Create(CultureInfo.InvariantCulture, $"/_lio/index-beatmapset/{beatmapsetId}"), cancellationToken)
            .ConfigureAwait(false);
        fallback.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// A newly submitted set, as osu!'s submission does (<c>POST /_lio/beatmapsets/{set}/broadcast-new</c>):
    /// the mapper's followers are notified and "… has submitted a new beatmap" goes into activity.
    /// </summary>
    public async Task BroadcastNewBeatmapsetAsync(int beatmapsetId, CancellationToken cancellationToken = default)
    {
        using var response = await PostAsync(
            string.Create(CultureInfo.InvariantCulture, $"/_lio/beatmapsets/{beatmapsetId}/broadcast-new?create_event=1"), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// A new version of a set (<c>POST /_lio/beatmapsets/{set}/broadcast-update</c>): "… has updated the
    /// beatmap" in activity, by <paramref name="userId"/> (who uploaded it, not necessarily its mapper).
    /// </summary>
    public async Task BroadcastUpdatedBeatmapsetAsync(int beatmapsetId, int userId, CancellationToken cancellationToken = default)
    {
        using var response = await PostAsync(
            string.Create(CultureInfo.InvariantCulture, $"/_lio/beatmapsets/{beatmapsetId}/broadcast-update?user_id={userId}"), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Rebuilds a country's ranking data on osu-web (the fork's <c>/_lio/tomyum/recalculate-country/{code}</c>).
    /// On osu! a scheduled job does this; a TomYum setup has no scheduler running, so TomYum asks after
    /// stats change. A 404 (a stock osu-web without the endpoint) is ignored.
    /// </summary>
    public async Task RecalculateCountryAsync(string countryAcronym, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(countryAcronym);

        using var response = await PostAsync($"/_lio/tomyum/recalculate-country/{Uri.EscapeDataString(countryAcronym.ToUpperInvariant())}", cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Posts an in-game message to website chat (the fork's <c>/_lio/tomyum/chat-message</c>): to the
    /// public channel <paramref name="channel"/>, or as a PM to <paramref name="targetId"/>. False when
    /// the channel doesn't exist on the website.
    /// </summary>
    public async Task<bool> SendChatMessageAsync(int senderId, string? channel, int? targetId, string message, bool isAction, CancellationToken cancellationToken = default)
    {
        using var response = await PostJsonAsync("/_lio/tomyum/chat-message", new
        {
            sender_id = senderId,
            channel,
            target_id = targetId,
            message,
            is_action = isAction,
        }, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// Gives a set on the website its osu! description (BBCode; the site keeps it as the set's
    /// description post), nominators (osu! users, shown with links to their osu! profiles) and
    /// featured artist track (the "featured artist" badge and listing filter), and who made the set and
    /// its difficulties on osu! (shown with their osu! profiles and avatars) when known.
    /// </summary>
    public async Task SyncBeatmapsetExtrasAsync(int beatmapsetId, BeatmapsetExtras extras, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extras);
        var body = new Dictionary<string, object?>
        {
            ["description"] = extras.DescriptionBbcode,
            ["nominators"] = extras.Nominators.Select(n => new { id = n.OsuUserId, username = n.Username, rulesets = n.Rulesets }),
            ["track_id"] = extras.TrackId,
        };
        // Sent only when osu! was asked: the website keeps what it has otherwise.
        if (extras.Mappers is { } mappers)
        {
            body["creator"] = extras.Creator is { } creator ? new { id = creator.OsuUserId, username = creator.Username } : null;
            body["mappers"] = mappers.ToDictionary(
                m => m.Key.ToString(CultureInfo.InvariantCulture),
                m => m.Value.Select(u => new { id = u.OsuUserId, username = u.Username }));
        }
        using var response = await PostJsonAsync(string.Create(CultureInfo.InvariantCulture, $"/_lio/tomyum/beatmapset-extras/{beatmapsetId}"), body, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Creates the game's public channels in website chat (the fork's <c>/_lio/tomyum/chat-channels</c>).</summary>
    public async Task EnsureChatChannelsAsync(IEnumerable<(string Name, string Description, bool Moderated)> channels, CancellationToken cancellationToken = default)
    {
        using var response = await PostJsonAsync("/_lio/tomyum/chat-channels", new
        {
            channels = channels.Select(c => new { name = c.Name, description = c.Description, moderated = c.Moderated }).ToList(),
        }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>The signed URL for <paramref name="path"/> at the current time.</summary>
    public (Uri Url, string Signature) Sign(string path)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("osu-web InterOp isn't configured (OsuWeb:InteropUrl / OsuWeb:InteropSecret).");

        var timestamp = _time.GetUtcNow().ToUnixTimeSeconds();
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = string.Create(CultureInfo.InvariantCulture, $"{_options.InteropUrl!.TrimEnd('/')}{path}{separator}timestamp={timestamp}");
        // HMAC-SHA1 is what osu-web verifies (LegacyInterOpAuth: hash_hmac('sha1', ...)); not our choice.
#pragma warning disable CA5350
        var hash = HMACSHA1.HashData(Encoding.UTF8.GetBytes(_options.InteropSecret!), Encoding.UTF8.GetBytes(url));
#pragma warning restore CA5350
        return (new Uri(url), Convert.ToHexStringLower(hash));
    }

    private async Task<HttpResponseMessage> PostAsync(string path, CancellationToken cancellationToken)
    {
        var (url, signature) = Sign(path);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-LIO-Signature", signature);
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A signed POST with a JSON body (osu-web's InterOp signs only the URL).</summary>
    private async Task<HttpResponseMessage> PostJsonAsync(string path, object body, CancellationToken cancellationToken)
    {
        var (url, signature) = Sign(path);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(body),
        };
        request.Headers.Add("X-LIO-Signature", signature);
        request.Headers.Accept.ParseAdd("application/json");
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
