using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;

namespace TomYum.Server.OsuWeb;

/// <summary>Where someone is online.</summary>
public enum OnlineClient
{
    Stable,
    Lazer,
    Website,
}

/// <summary>A player who's online, on whichever client.</summary>
/// <param name="UserId">The player.</param>
/// <param name="Username">Their name.</param>
/// <param name="Client">Where they're online.</param>
/// <param name="Doing">What they're doing, in words ("Choosing a beatmap"), or null when idle or unknown.</param>
/// <param name="BeatmapId">The map they're on, if known.</param>
/// <param name="Mode">Their ruleset (stable: the selected mode; lazer: while playing), or null.</param>
/// <param name="LastSeen">When they were last active (now, for lazer).</param>
/// <param name="Playing">Whether they're playing a map right now.</param>
public sealed record OnlineEntry(int UserId, string Username, OnlineClient Client, string? Doing, int? BeatmapId, int? Mode, DateTime LastSeen, bool Playing);

/// <summary>
/// Everyone online: in stable (the game server's sessions), on lazer (ppy's spectator server's
/// presence, <see cref="LazerPresence"/>), and on the website (seen there within osu-web's online
/// window). Each player once, stable first, then lazer. BanchoBot and restricted players are left out.
/// </summary>
public sealed class OnlinePlayers
{
    private readonly PlayerRegistry _players;
    private readonly IServiceScopeFactory _scopes;
    private readonly LazerPresence? _lazer;

    public OnlinePlayers(PlayerRegistry players, IServiceScopeFactory scopes, LazerPresence? lazer = null)
    {
        _players = players;
        _scopes = scopes;
        _lazer = lazer;
    }

    public async Task<IReadOnlyList<OnlineEntry>> GetAsync(CancellationToken cancellationToken = default)
    {
        var online = _players.All
            .Where(p => p.UserId != BanchoBot.UserId && !p.IsRestricted)
            .OrderBy(p => p.Presence.Username, StringComparer.OrdinalIgnoreCase)
            .Select(p => new OnlineEntry(p.UserId, p.Presence.Username, OnlineClient.Stable,
                string.IsNullOrEmpty(p.Presence.ActionInfo) ? null : p.Presence.ActionInfo,
                p.Presence.BeatmapId > 0 ? p.Presence.BeatmapId : null, p.Presence.Mode,
                p.LastActivityUtc.UtcDateTime, p.Presence.Action is 2 or 12))
            .ToList();
        var seen = online.Select(p => p.UserId).ToHashSet();

        using var scope = _scopes.CreateScope();
        var community = scope.ServiceProvider.GetService<OsuWebCommunity>();

        if (_lazer is not null && community is not null)
        {
            var lazer = (await _lazer.GetOnlineAsync().ConfigureAwait(false)).Where(p => p.UserId != BanchoBot.UserId && !seen.Contains(p.UserId)).ToList();
            var names = await community.GetVisibleNamesAsync(lazer.Select(p => p.UserId).ToList(), cancellationToken).ConfigureAwait(false);
            // Active right now; a moment back, so a clock a second ahead elsewhere doesn't say "in a few seconds".
            var now = DateTime.UtcNow.AddSeconds(-5);
            foreach (var player in lazer.Where(p => names.ContainsKey(p.UserId)).OrderBy(p => names[p.UserId], StringComparer.OrdinalIgnoreCase))
            {
                online.Add(new OnlineEntry(player.UserId, names[player.UserId], OnlineClient.Lazer,
                    player.Text is null ? null : player.Describe(), player.BeatmapId, player.RulesetId, now, player.IsPlaying));
                seen.Add(player.UserId);
            }
        }

        if (community is not null)
        {
            foreach (var (userId, username, lastSeen) in await community.GetOnlineSinceAsync(cancellationToken).ConfigureAwait(false))
            {
                if (seen.Add(userId))
                    online.Add(new OnlineEntry(userId, username, OnlineClient.Website, null, null, null, lastSeen, false));
            }
        }
        return online;
    }
}
