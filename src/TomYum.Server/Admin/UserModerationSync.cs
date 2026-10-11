using TomYum.Bancho;
using TomYum.Caching;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;

namespace TomYum.Server.Admin;

/// <summary>What <see cref="UserModerationSync.RefreshAsync"/> found and applied.</summary>
public sealed record UserRefreshResult(int UserId, bool Restricted, DateTimeOffset? SilenceEndUtc, bool Online, bool Reconnected);

/// <summary>
/// Applies staff changes made on the website to the game, live (GMT/Admin panels call it after
/// every change): restriction (hide from others and leaderboards; BanchoBot says why), silences
/// (countdown for the player, others hide their messages), and a reconnect when the restriction, name or
/// privileges changed. Also re-indexes the player's website scores when the restriction changed.
/// </summary>
public sealed class UserModerationSync
{
    private readonly IUserRepository _users;
    private readonly PlayerRegistry _players;
    private readonly RestrictedUsers _restricted;
    private readonly TimeProvider _time;
    private readonly ILogger<UserModerationSync> _logger;
    private readonly OsuWebScoreMirror? _mirror;
    private readonly OsuWebScoreIndexing? _indexing;
    private readonly RestrictionNotices? _notices;

    public UserModerationSync(
        IUserRepository users,
        PlayerRegistry players,
        RestrictedUsers restricted,
        TimeProvider time,
        ILogger<UserModerationSync> logger,
        OsuWebScoreMirror? mirror = null,
        OsuWebScoreIndexing? indexing = null,
        RestrictionNotices? notices = null)
    {
        _users = users;
        _players = players;
        _restricted = restricted;
        _time = time;
        _logger = logger;
        _mirror = mirror;
        _indexing = indexing;
        _notices = notices;
    }

    /// <summary>Reloads user <paramref name="userId"/> and applies it; null when there's no such user.</summary>
    public async Task<UserRefreshResult?> RefreshAsync(int userId, CancellationToken cancellationToken = default)
    {
        var user = await _users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return null;

        var restrictionChanged = _restricted.Contains(userId) != user.IsRestricted;
        _restricted.Set(userId, user.IsRestricted);

        string? reconnect = null;
        var player = _players.GetByUserId(userId);
        if (player is not null)
        {
            var now = _time.GetUtcNow();

            // Restricted, or no longer: everyone else's user list now (the player's own client learns it
            // when it logs in again, below; BanchoBot tells them why).
            var restrictionFlipped = user.IsRestricted != player.IsRestricted;
            if (user.IsRestricted && !player.IsRestricted)
            {
                player.IsRestricted = true;
                var gone = new BanchoPacketWriter(16);
                gone.WriteInt32Packet(ServerPacketId.UserLogout, userId);
                _players.EnqueueToAll(gone.ToArray(), exceptUserId: userId);
            }
            else if (!user.IsRestricted && player.IsRestricted)
            {
                player.IsRestricted = false;
                var back = new BanchoPacketWriter(128);
                LoginResponseBuilder.WritePresence(back, player.Presence);
                LoginResponseBuilder.WriteStats(back, player.Presence);
                _players.EnqueueToAll(back.ToArray(), exceptUserId: userId);
            }

            var wasSilenced = player.IsSilenced(now);
            player.SilenceEndUtc = user.SilenceEndUtc;
            var secondsLeft = player.SilenceSecondsLeft(now);
            if (wasSilenced || secondsLeft > 0)
            {
                var silence = new BanchoPacketWriter(16);
                ModerationPackets.WriteSilenceEnd(silence, secondsLeft);
                player.Enqueue(silence.ToArray());
            }
            if (!wasSilenced && secondsLeft > 0)
            {
                var others = new BanchoPacketWriter(16);
                ModerationPackets.WriteUserSilenced(others, userId);
                _players.EnqueueToAll(others.ToArray(), exceptUserId: userId);
            }

            // A restriction, a new name or new privileges only take effect on login: the client reconnects
            // (after BanchoBot's message, below, so that arrives first).
            if (restrictionFlipped)
            {
                reconnect = string.Empty;
            }
            else if (!string.Equals(player.Presence.Username, user.Username, StringComparison.Ordinal)
                || player.Presence.Privileges != PrivilegeMapper.ToBancho(user.Privileges))
            {
                reconnect = "Your account was updated by staff; reconnecting.";
            }
        }

        if (restrictionChanged)
            await ReindexWebsiteScoresAsync(userId, cancellationToken).ConfigureAwait(false);

        // BanchoBot tells them (once per restriction, and when it's lifted).
        if (_notices is not null)
            await _notices.NotifyAsync(userId, user.Username, user.IsRestricted, cancellationToken).ConfigureAwait(false);

        if (player is not null && reconnect is not null)
            player.RequestReconnect(reconnect);

        return new UserRefreshResult(userId, user.IsRestricted, user.SilenceEndUtc, player is not null, reconnect is not null);
    }

    /// <summary>The website's ranks and score search leave restricted players out; bring them up to date.</summary>
    private async Task ReindexWebsiteScoresAsync(int userId, CancellationToken cancellationToken)
    {
        if (_mirror is null)
            return;

        try
        {
            foreach (var mode in new[] { 0, 1, 2, 3 })
                await _mirror.RecomputeGlobalRanksAsync(mode, cancellationToken).ConfigureAwait(false);
            if (_indexing is not null)
                await _indexing.QueueAsync(await _mirror.GetLinkedScoreIdsAsync(userId, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Couldn't update osu-web ranks/search after user {UserId}'s restriction changed.", userId);
        }
    }
}
