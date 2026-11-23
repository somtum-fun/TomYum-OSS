using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Spectate;

/// <summary>
/// Shared spectating logic: attaching/detaching a spectator to a host and the resulting packet
/// fan-out. Used by the four spectate handlers and by logout teardown, so the behaviour lives in
/// one place. Clean-room port of bancho.py's <c>add_spectator</c>/<c>remove_spectator</c>
/// (see <c>memories/protocols/</c>): the core "watch a player" flow (join/leave notifications +
/// frame relay) plus the ephemeral <c>#spectator</c> chat channel the host and its spectators share.
/// </summary>
/// <remarks>
/// Every packet is enqueued via <see cref="PlayerRegistry"/> (never written to a response), so this
/// is safe to call from any context — including logout, where the originator must not receive
/// anything. Bytes queued onto the originator's own session are flushed in the same poll.
/// </remarks>
internal static class SpectatorManager
{
    /// <summary>The client-facing name of every host's ephemeral spectator channel.</summary>
    private const string SpectatorChannelName = "#spectator";

    // The #spectator join/kick packets are constant (a fixed channel name), so frame them once.
    // Enqueue copies the bytes, so sharing these read-only arrays across sessions is safe.
    private static readonly byte[] SpectatorChannelJoin =
        new BanchoPacketWriter(32).WriteStringPacket(ServerPacketId.ChannelJoinSuccess, SpectatorChannelName).ToArray();

    private static readonly byte[] SpectatorChannelKick =
        new BanchoPacketWriter(32).WriteStringPacket(ServerPacketId.ChannelKick, SpectatorChannelName).ToArray();

    // The six server packets are all a single i32 (the subject user id) or a raw relay.
    private static byte[] SpectatorJoined(int uid) =>
        new BanchoPacketWriter(16).WriteInt32Packet(ServerPacketId.SpectatorJoined, uid).ToArray();

    private static byte[] SpectatorLeft(int uid) =>
        new BanchoPacketWriter(16).WriteInt32Packet(ServerPacketId.SpectatorLeft, uid).ToArray();

    private static byte[] FellowSpectatorJoined(int uid) =>
        new BanchoPacketWriter(16).WriteInt32Packet(ServerPacketId.FellowSpectatorJoined, uid).ToArray();

    private static byte[] FellowSpectatorLeft(int uid) =>
        new BanchoPacketWriter(16).WriteInt32Packet(ServerPacketId.FellowSpectatorLeft, uid).ToArray();

    /// <summary>Encodes SPECTATOR_CANT_SPECTATE (22) naming <paramref name="uid"/>.</summary>
    public static byte[] SpectatorCantSpectate(int uid) =>
        new BanchoPacketWriter(16).WriteInt32Packet(ServerPacketId.SpectatorCantSpectate, uid).ToArray();

    /// <summary>
    /// Attaches <paramref name="spectator"/> to <paramref name="host"/>: notifies the pre-existing
    /// spectators and the joining player about each other (FELLOW_SPECTATOR_JOINED both ways), tells
    /// the host (SPECTATOR_JOINED), then records the link. Caller ensures the two are distinct and
    /// that any previous host was detached first.
    /// </summary>
    public static void Attach(OnlinePlayer host, OnlinePlayer spectator, PlayerRegistry registry)
    {
        var isFirstSpectator = host.SpectatorCount == 0;

        var fellowJoined = FellowSpectatorJoined(spectator.UserId);
        foreach (var existingId in host.Spectators) // existing spectators, before this one is added
        {
            foreach (var session in registry.SpectatingSessions(existingId, host.UserId))
                session.Enqueue(fellowJoined);                            // they learn about the newcomer
            spectator.Enqueue(FellowSpectatorJoined(existingId));         // newcomer learns about them
        }

        host.Enqueue(SpectatorJoined(spectator.UserId));                  // the host learns a spectator joined

        host.AddSpectator(spectator.UserId);
        spectator.SpectatingUserId = host.UserId;

        // Ephemeral #spectator chat channel (mirrors Match.Channel): the host joins its own channel
        // when the first spectator arrives; every spectator joins on attach.
        if (isFirstSpectator)
            JoinSpectatorChannel(host, host);
        JoinSpectatorChannel(host, spectator);
    }

    /// <summary>
    /// Detaches <paramref name="spectator"/> from <paramref name="host"/>: clears the link, notifies
    /// the remaining spectators (FELLOW_SPECTATOR_LEFT), and tells the host (SPECTATOR_LEFT).
    /// Idempotent — a no-op fan-out still runs, but callers guard on the link existing.
    /// </summary>
    public static void Detach(OnlinePlayer host, OnlinePlayer spectator, PlayerRegistry registry)
    {
        host.RemoveSpectator(spectator.UserId);
        spectator.SpectatingUserId = null;

        // Leave the ephemeral #spectator channel (client closes the tab on CHANNEL_KICK).
        LeaveSpectatorChannel(host, spectator);

        var fellowLeft = FellowSpectatorLeft(spectator.UserId);
        foreach (var remainingId in host.Spectators) // now excludes the leaver
            foreach (var session in registry.SpectatingSessions(remainingId, host.UserId))
                session.Enqueue(fellowLeft);

        host.Enqueue(SpectatorLeft(spectator.UserId));

        // When the last spectator leaves, the host leaves its own channel too.
        if (host.SpectatorCount == 0)
        {
            LeaveSpectatorChannel(host, host);
            host.SpectatorChannel.Locked = false; // a !lock is for that spectating, not the host's next one
        }
    }

    /// <summary>
    /// BanchoBot starts watching <paramref name="host"/> for a plugin (osu!dan): the host and its
    /// spectators see BanchoBot join, so the host's client streams frames. BanchoBot itself keeps no
    /// link (it can watch many players at once): it isn't in the #spectator channel, and the frame
    /// fan-out finds no session of it, so nothing is queued for it. Idempotent.
    /// </summary>
    /// <returns>True when it was added, false when it was already watching.</returns>
    public static bool AttachBot(OnlinePlayer host, PlayerRegistry registry)
    {
        if (host.Spectators.Contains(BanchoBot.UserId))
        {
            // Watching quietly with nobody shown in its place: the client has no spectator, so it's told now.
            if (host.GetState<bool>(QuietBotKey) && ShownWatchers(host).Length == 0)
            {
                host.SetState(QuietBotKey, false);
                host.Enqueue(SpectatorJoined(BanchoBot.UserId));
            }

            return false;
        }

        var isFirstSpectator = host.SpectatorCount == 0;
        var fellowJoined = FellowSpectatorJoined(BanchoBot.UserId);
        foreach (var existingId in host.Spectators)
            foreach (var session in registry.SpectatingSessions(existingId, host.UserId))
                session.Enqueue(fellowJoined);

        host.Enqueue(SpectatorJoined(BanchoBot.UserId));
        host.AddSpectator(BanchoBot.UserId);
        if (isFirstSpectator)
            JoinSpectatorChannel(host, host);
        return true;
    }

    /// <summary>
    /// <see cref="AttachBot"/> without telling anyone: the link is recorded (frames reach plugins) but the host's client
    /// isn't told BanchoBot joined. For a plugin that shows who really watches (<see cref="ShowWatcher"/>) instead.
    /// </summary>
    /// <returns>True when it was added, false when it was already watching.</returns>
    public static bool AttachBotQuietly(OnlinePlayer host)
    {
        if (!host.AddSpectator(BanchoBot.UserId))
            return false;
        host.SetState(QuietBotKey, true);
        return true;
    }

    /// <summary>The session state key set while BanchoBot watches <see cref="AttachBotQuietly">quietly</see>.</summary>
    internal const string QuietBotKey = "tomyum.spectate.quiet-bot";

    /// <summary>The session state key holding the watchers <see cref="ShowWatcher"/> shows the host.</summary>
    internal const string ShownWatchersKey = "tomyum.spectate.shown-watchers";

    /// <summary>
    /// Shows <paramref name="host"/> that <paramref name="watcher"/> (an account watching from osu!lazer) spectates them:
    /// their presence (so the client can name them), then SPECTATOR_JOINED. Nothing is linked: frames come through
    /// BanchoBot, and nothing is queued for the watcher.
    /// </summary>
    /// <returns>True when shown now, false when they already were.</returns>
    public static bool ShowWatcher(OnlinePlayer host, BanchoPresence watcher)
    {
        var shown = host.GetState<HashSet<int>>(ShownWatchersKey) ?? [];
        lock (shown)
        {
            if (!shown.Add(watcher.UserId))
                return false;
        }

        host.SetState(ShownWatchersKey, shown);
        var writer = new BanchoPacketWriter(128);
        LoginResponseBuilder.WritePresence(writer, watcher);
        host.Enqueue(writer.ToArray());
        host.Enqueue(SpectatorJoined(watcher.UserId));
        return true;
    }

    /// <summary>
    /// <paramref name="host"/> stops seeing <paramref name="watcherUserId"/> (<see cref="ShowWatcher"/>'s reverse). When it
    /// was the last one and BanchoBot watches quietly, the host is told BanchoBot joined, so their client keeps streaming.
    /// </summary>
    /// <returns>True when they were shown.</returns>
    public static bool HideWatcher(OnlinePlayer host, int watcherUserId, PlayerRegistry registry)
    {
        if (host.GetState<HashSet<int>>(ShownWatchersKey) is not { } shown)
            return false;

        bool last;
        lock (shown)
        {
            if (!shown.Remove(watcherUserId))
                return false;
            last = shown.Count == 0;
        }

        host.Enqueue(SpectatorLeft(watcherUserId));
        // Shown only so the client could name them: out of its online list again (unless they're on osu!stable).
        if (registry.GetByUserId(watcherUserId) is null)
            host.Enqueue(new BanchoPacketWriter(16).WriteInt32Packet(ServerPacketId.UserLogout, watcherUserId).ToArray());
        if (last && host.GetState<bool>(QuietBotKey) && host.Spectators.Contains(BanchoBot.UserId))
        {
            host.SetState(QuietBotKey, false);
            host.Enqueue(SpectatorJoined(BanchoBot.UserId));
        }

        return true;
    }

    /// <summary>The watchers <see cref="ShowWatcher"/> shows <paramref name="host"/> now.</summary>
    public static int[] ShownWatchers(OnlinePlayer host)
    {
        if (host.GetState<HashSet<int>>(ShownWatchersKey) is not { } shown)
            return [];
        lock (shown)
            return [.. shown];
    }

    /// <summary>BanchoBot stops watching <paramref name="host"/> (<see cref="AttachBot"/>'s reverse).</summary>
    /// <returns>True when it was watching.</returns>
    public static bool DetachBot(OnlinePlayer host, PlayerRegistry registry)
    {
        if (!host.RemoveSpectator(BanchoBot.UserId))
            return false;

        if (host.GetState<bool>(QuietBotKey))
        {
            // It was never shown: nothing to take back.
            host.SetState(QuietBotKey, false);
            return true;
        }

        var fellowLeft = FellowSpectatorLeft(BanchoBot.UserId);
        foreach (var remainingId in host.Spectators)
            foreach (var session in registry.SpectatingSessions(remainingId, host.UserId))
                session.Enqueue(fellowLeft);

        host.Enqueue(SpectatorLeft(BanchoBot.UserId));
        if (host.SpectatorCount == 0)
        {
            LeaveSpectatorChannel(host, host);
            host.SpectatorChannel.Locked = false; // a !lock is for that spectating, not the host's next one
        }
        return true;
    }

    /// <summary>Adds <paramref name="member"/> to the host's #spectator channel and opens the client tab.</summary>
    private static void JoinSpectatorChannel(OnlinePlayer host, OnlinePlayer member)
    {
        host.SpectatorChannel.Join(member);
        member.JoinChannel(SpectatorChannelName);
        member.Enqueue(SpectatorChannelJoin);
    }

    /// <summary>Removes <paramref name="member"/> from the host's #spectator channel and closes the client tab.</summary>
    private static void LeaveSpectatorChannel(OnlinePlayer host, OnlinePlayer member)
    {
        host.SpectatorChannel.Leave(member.UserId);
        member.LeaveChannel(SpectatorChannelName);
        member.Enqueue(SpectatorChannelKick);
    }

    /// <summary>
    /// Re-announces an existing spectator that re-sent START_SPECTATING for the same host (the
    /// "I finished downloading the map" case): re-notifies the host and the other spectators without
    /// re-adding anything.
    /// </summary>
    public static void Reannounce(OnlinePlayer host, OnlinePlayer spectator, PlayerRegistry registry)
    {
        host.Enqueue(SpectatorJoined(spectator.UserId));

        var fellowJoined = FellowSpectatorJoined(spectator.UserId);
        foreach (var otherId in host.Spectators)
            if (otherId != spectator.UserId)
                foreach (var session in registry.SpectatingSessions(otherId, host.UserId))
                    session.Enqueue(fellowJoined);
    }

    /// <summary>
    /// Tears down all spectate links for a player leaving (logout/reap): stops spectating its host,
    /// and drains its own spectators (bancho.py fork behaviour). Safe to call unconditionally.
    /// </summary>
    public static void TeardownForLeaver(OnlinePlayer leaver, PlayerRegistry registry)
    {
        if (leaver.SpectatingUserId is int hostId)
        {
            var host = registry.GetByUserId(hostId);
            if (host is not null)
                Detach(host, leaver, registry);
            else
                leaver.SpectatingUserId = null;
        }

        foreach (var spectatorId in leaver.Spectators) // snapshot; Detach mutates the set
        {
            var sessions = registry.SpectatingSessions(spectatorId, leaver.UserId).ToList();
            foreach (var spectator in sessions)
                Detach(leaver, spectator, registry);
            if (sessions.Count == 0)
                leaver.RemoveSpectator(spectatorId);
        }
    }
}
