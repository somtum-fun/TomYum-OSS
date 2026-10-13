using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>Helpers for broadcasting match-state packets to match players and lobby viewers.</summary>
internal static class MatchBroadcast
{
    /// <summary>Sends <paramref name="bytes"/> to every player currently in <paramref name="match"/>.</summary>
    /// <param name="match">The match.</param>
    /// <param name="registry">The online players.</param>
    /// <param name="bytes">What its players get.</param>
    /// <param name="watcherBytes">What tournament client windows following it get instead, when
    /// <paramref name="bytes"/> carries the room's password (they never get it); empty: nothing.</param>
    internal static void ToMatch(Bancho.Match match, PlayerRegistry registry, byte[] bytes, byte[]? watcherBytes = null)
    {
        lock (match.SyncRoot)
        {
            foreach (var slot in match.Slots)
                if (slot.HasPlayer)
                    registry.GetByUserId(slot.UserId)?.Enqueue(bytes);
            if (watcherBytes is not { Length: 0 })
            {
                foreach (var watcher in match.TournamentWatchers)
                    watcher.Enqueue(watcherBytes ?? bytes);
            }
        }
    }

    /// <summary>Sends <paramref name="bytes"/> to every online player who has the lobby open.</summary>
    internal static void ToLobby(PlayerRegistry registry, byte[] bytes)
    {
        foreach (var p in registry.All)
            if (p.InLobby) p.Enqueue(bytes);
    }

    /// <summary>Writes UPDATE_MATCH (26) and fans it out to all players in the match and all lobby viewers.</summary>
    internal static void UpdateMatch(Bancho.Match match, PlayerRegistry registry)
    {
        var inMatch = EncodeMatch(ServerPacketId.UpdateMatch, match, sendPassword: true);
        var inLobby = EncodeMatch(ServerPacketId.UpdateMatch, match, sendPassword: false);
        ToMatch(match, registry, inMatch, inLobby);
        foreach (var p in registry.All)
            if (p.InLobby && !IsInMatch(p.UserId, match))
                p.Enqueue(inLobby);
    }

    /// <summary>Encodes a match packet to bytes.</summary>
    internal static byte[] EncodeMatch(ServerPacketId id, Bancho.Match match, bool sendPassword)
    {
        var w = new BanchoPacketWriter(256);
        MatchEncoder.WriteMatchPacket(w, id, match, sendPassword);
        return w.ToArray();
    }

    /// <summary>True when <paramref name="userId"/> occupies a slot in <paramref name="match"/>.</summary>
    internal static bool IsInMatch(int userId, Bancho.Match match)
    {
        foreach (var s in match.Slots)
            if (s.UserId == userId) return true;
        return false;
    }

    /// <summary>
    /// Finalises a match: sets InProgress to false, resets all Complete slots to NotReady,
    /// broadcasts MATCH_COMPLETE (58) to every player in the room, and sends UPDATE_MATCH to
    /// both match players and lobby so every client reflects the new idle state.
    /// </summary>
    internal static void CompleteMatch(Bancho.Match match, PlayerRegistry registry)
    {
        lock (match.SyncRoot)
        {
            match.InProgress = false;
            foreach (var s in match.Slots)
                if (s.Status == SlotStatus.Complete)
                    s.Status = SlotStatus.NotReady;
        }

        match.History?.GameEnded(match);

        var completeBytes = new BanchoPacketWriter(16)
            .WritePacket(ServerPacketId.MatchComplete, null)
            .ToArray();
        ToMatch(match, registry, completeBytes);
        UpdateMatch(match, registry);
    }

    /// <summary>
    /// Removes <paramref name="player"/> from <paramref name="match"/>: clears their slot, leaves the
    /// match channel, sends CHANNEL_KICK #multiplayer, transfers host if needed, disbands an empty room,
    /// and auto-completes a match in-progress if all remaining players are done.
    /// </summary>
    /// <returns><c>true</c> when the room was disbanded (caller should not call UpdateMatch).</returns>
    internal static bool RemovePlayer(
        Bancho.Match match, OnlinePlayer player, PlayerRegistry registry, MatchRegistry matches)
    {
        // Remove from per-match chat channel first so they don't receive the UpdateMatch we're about to send.
        match.Channel.Leave(player.UserId);
        player.LeaveChannel("#multiplayer");

        // Send CHANNEL_KICK so the client closes the #multiplayer tab and navigates back to the lobby.
        player.Enqueue(new BanchoPacketWriter(16)
            .WriteStringPacket(ServerPacketId.ChannelKick, "#multiplayer")
            .ToArray());

        bool matchEnded;
        int? newHost = null;
        lock (match.SyncRoot)
        {
            var slotIdx = match.IndexOf(player.UserId);
            if (slotIdx >= 0)
            {
                match.Slots[slotIdx].Reset();
                match.History?.Left(match, player.UserId);
            }

            var remaining = match.PlayerIds();
            if (remaining.Count == 0)
            {
                // Room is empty — cancel any countdown, disband, notify lobby.
                match.CountdownCts?.Cancel();
                match.CountdownCts = null;
                matches.Remove(match.Id);
                match.History?.Disbanded(match);

                var disposeBytes = new BanchoPacketWriter(16)
                    .WriteInt32Packet(ServerPacketId.DisposeMatch, match.Id)
                    .ToArray();
                ToLobby(registry, disposeBytes);
                return true;
            }

            // Transfer host if needed.
            if (match.HostUserId == player.UserId)
            {
                match.HostUserId = remaining[0];
                newHost = remaining[0];
            }

            // Check whether removing this player finishes the match; state reset is deferred to CompleteMatch.
            matchEnded = match.InProgress && match.AllComplete();
        }

        if (newHost is { } host)
            match.History?.HostChanged(match, host);

        if (matchEnded)
            CompleteMatch(match, registry);
        else
            UpdateMatch(match, registry);

        return false;
    }
}
