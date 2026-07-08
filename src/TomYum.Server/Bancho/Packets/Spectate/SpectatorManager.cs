using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Spectate;

/// <summary>
/// Shared spectating logic: attaching/detaching a spectator to a host and the resulting packet
/// fan-out. Used by the four spectate handlers and by logout teardown, so the behaviour lives in
/// one place. Clean-room port of bancho.py's <c>add_spectator</c>/<c>remove_spectator</c>
/// (see <c>memories/protocols/</c>); the ephemeral <c>#spectator</c> chat channel is a later slice —
/// this covers the core "watch a player" flow (join/leave notifications + frame relay).
/// </summary>
/// <remarks>
/// Every packet is enqueued via <see cref="PlayerRegistry"/> (never written to a response), so this
/// is safe to call from any context — including logout, where the originator must not receive
/// anything. Bytes queued onto the originator's own session are flushed in the same poll.
/// </remarks>
internal static class SpectatorManager
{
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
        var fellowJoined = FellowSpectatorJoined(spectator.UserId);
        foreach (var existingId in host.Spectators) // existing spectators, before this one is added
        {
            registry.GetByUserId(existingId)?.Enqueue(fellowJoined);      // they learn about the newcomer
            spectator.Enqueue(FellowSpectatorJoined(existingId));         // newcomer learns about them
        }

        host.Enqueue(SpectatorJoined(spectator.UserId));                  // the host learns a spectator joined

        host.AddSpectator(spectator.UserId);
        spectator.SpectatingUserId = host.UserId;
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

        var fellowLeft = FellowSpectatorLeft(spectator.UserId);
        foreach (var remainingId in host.Spectators) // now excludes the leaver
            registry.GetByUserId(remainingId)?.Enqueue(fellowLeft);

        host.Enqueue(SpectatorLeft(spectator.UserId));
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
                registry.GetByUserId(otherId)?.Enqueue(fellowJoined);
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
            var spectator = registry.GetByUserId(spectatorId);
            if (spectator is not null)
                Detach(leaver, spectator, registry);
            else
                leaver.RemoveSpectator(spectatorId);
        }
    }
}
