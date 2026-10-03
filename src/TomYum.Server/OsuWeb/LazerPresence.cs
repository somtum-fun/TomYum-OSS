using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace TomYum.Server.OsuWeb;

/// <summary>A player online on lazer, as ppy's spectator server sees them.</summary>
/// <param name="UserId">The player.</param>
/// <param name="Status">Online or DoNotDisturb.</param>
/// <param name="Activity">What they're doing (lazer's activity type, e.g. <c>InSoloGame</c>, <c>ChoosingBeatmap</c>), or null when idle.</param>
/// <param name="Text">The activity as lazer words it ("Choosing a beatmap", "Playing").</param>
/// <param name="Details">The map, room or player it's about, if any.</param>
/// <param name="BeatmapId">The map they're on, if any.</param>
/// <param name="RulesetId">The ruleset they're playing, while playing.</param>
public sealed record LazerPlayer(int UserId, string? Status, string? Activity, string? Text, string? Details, int? BeatmapId, int? RulesetId)
{
    /// <summary>Whether they're playing (solo, multiplayer, playlists, daily challenge).</summary>
    public bool IsPlaying => Activity is "InSoloGame" or "InMultiplayerGame" or "InPlaylistGame" or "PlayingDailyChallenge";

    /// <summary>"Playing: Artist - Title [Version]", or just the activity.</summary>
    public string Describe() =>
        Text is null ? "Idle" : string.IsNullOrEmpty(Details) ? Text : $"{Text}: {Details}";
}

/// <summary>
/// Who's online on lazer, from the presence feed TomYum's build of ppy's spectator server writes to
/// Redis (<c>tomyum:lazer-presence</c>, refreshed every 5 seconds). Empty without Redis or when the
/// spectator server isn't publishing.
/// </summary>
public sealed class LazerPresence
{
    public const string Key = "tomyum:lazer-presence";

    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<LazerPresence> _logger;

    public LazerPresence(ILogger<LazerPresence> logger, IConnectionMultiplexer? redis = null)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LazerPlayer>> GetOnlineAsync() =>
        await TryGetOnlineAsync().ConfigureAwait(false) ?? [];

    /// <summary>Like <see cref="GetOnlineAsync"/>, but null (not empty) without Redis or when reading it failed.</summary>
    public async Task<IReadOnlyList<LazerPlayer>?> TryGetOnlineAsync()
    {
        if (_redis is null)
            return null;
        try
        {
            var entries = await _redis.GetDatabase().HashGetAllAsync(Key).ConfigureAwait(false);
            return Parse(entries.Select(e => ((string?)e.Name, (string?)e.Value)));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Reading lazer presence from Redis failed.");
            return null;
        }
    }

    /// <summary>The feed's entries (user id → JSON) as players; unreadable ones are skipped.</summary>
    public static IReadOnlyList<LazerPlayer> Parse(IEnumerable<(string? UserId, string? Json)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var players = new List<LazerPlayer>();
        foreach (var (userId, json) in entries)
        {
            if (!int.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || string.IsNullOrEmpty(json))
                continue;
            try
            {
                if (JsonSerializer.Deserialize<Entry>(json) is { } entry)
                    players.Add(new LazerPlayer(id, entry.Status, entry.Activity, entry.Text, entry.Details, entry.BeatmapId, entry.RulesetId));
            }
            catch (JsonException)
            {
                // A newer or older feed format: leave that player out.
            }
        }
        return players.OrderBy(p => p.UserId).ToList();
    }

    private sealed class Entry
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("activity")]
        public string? Activity { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("details")]
        public string? Details { get; set; }

        [JsonPropertyName("beatmap_id")]
        public int? BeatmapId { get; set; }

        [JsonPropertyName("ruleset_id")]
        public int? RulesetId { get; set; }
    }
}
