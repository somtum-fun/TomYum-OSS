using TomYum.Bancho;

namespace TomYum.Server.Bancho;

/// <summary>
/// Writes the osu!stable match composite (type 13) into a <see cref="BanchoPacketWriter"/>.
/// Wire layout: §6 of <c>memories/protocols/bancho-stable.md</c>.
/// </summary>
public static class MatchEncoder
{
    /// <summary>
    /// Writes a NEW_MATCH (27), UPDATE_MATCH (26), or MATCH_JOIN_SUCCESS (36) packet,
    /// whichever <paramref name="packetId"/> specifies, using the current match state.
    /// </summary>
    public static void WriteMatchPacket(BanchoPacketWriter w, ServerPacketId packetId, Match match, bool sendPassword)
    {
        w.WritePacket(packetId, pw => WriteMatchComposite(pw, match, sendPassword));
    }

    /// <summary>Reads a match composite from the client payload and applies it to <paramref name="match"/>.</summary>
    public static void ReadMatchComposite(BanchoPacketReader reader, Match match)
    {
        _ = reader.ReadUInt16(); // match id (ignored; set by server)
        _ = reader.ReadSByte(); // in_progress (ignored)
        _ = reader.ReadByte();  // powerplay byte (reserved)
        match.Mods    = reader.ReadUInt32();
        match.Name    = reader.ReadString();
        var pw        = reader.ReadString();
        if (!string.IsNullOrEmpty(pw)) match.Password = pw;
        match.MapName = reader.ReadString();
        match.MapId   = reader.ReadInt32();
        match.MapMd5  = reader.ReadString();

        var statuses = new byte[16];
        var teams    = new byte[16];
        for (var i = 0; i < 16; i++) statuses[i] = reader.ReadByte();
        for (var i = 0; i < 16; i++) teams[i]    = reader.ReadByte();

        for (var i = 0; i < 16; i++)
        {
            var status = (SlotStatus)statuses[i];
            if ((status & SlotStatus.HasPlayer) != 0)
                _ = reader.ReadInt32(); // user id — ignored here; slot membership is tracked server-side
            match.Slots[i].Status = status;
            match.Slots[i].Team   = teams[i];
        }

        _ = reader.ReadInt32(); // host id (ignored; set by server)
        match.Mode         = reader.ReadByte();
        match.WinCondition = (MatchWinCondition)reader.ReadByte();
        match.TeamType     = (MatchTeamType)reader.ReadByte();
        match.FreeMods     = reader.ReadByte() != 0;

        if (match.FreeMods)
            for (var i = 0; i < 16; i++)
                match.Slots[i].Mods = reader.ReadUInt32();

        match.Seed = reader.ReadInt32();
    }

    /// <summary>
    /// Reads only the fields needed to create a new match from a CREATE_MATCH payload
    /// and returns them as a <see cref="MatchCreateArgs"/>.
    /// </summary>
    public static MatchCreateArgs ReadCreateArgs(ReadOnlySpan<byte> payload)
    {
        var r = new BanchoPacketReader(payload);
        _ = r.ReadUInt16(); // match id
        _ = r.ReadSByte();  // in_progress
        _ = r.ReadByte();   // powerplay
        var mods     = r.ReadUInt32();
        var name     = r.ReadString();
        var password = r.ReadString();
        var mapName  = r.ReadString();
        var mapId    = r.ReadInt32();
        var mapMd5   = r.ReadString();

        // skip 16 slot_status + 16 slot_team + player ids (none at creation normally)
        var statuses = new byte[16];
        for (var i = 0; i < 16; i++) statuses[i] = r.ReadByte();
        for (var i = 0; i < 16; i++) _ = r.ReadByte(); // teams
        for (var i = 0; i < 16; i++)
            if (((SlotStatus)statuses[i] & SlotStatus.HasPlayer) != 0)
                _ = r.ReadInt32();

        _ = r.ReadInt32(); // host id
        var mode         = r.ReadByte();
        var winCondition = (MatchWinCondition)r.ReadByte();
        var teamType     = (MatchTeamType)r.ReadByte();
        var freeMods     = r.ReadByte() != 0;
        if (freeMods)
            for (var i = 0; i < 16; i++) _ = r.ReadUInt32();
        var seed = r.ReadInt32();

        return new MatchCreateArgs(name, password, mapName, mapId, mapMd5,
            mode, winCondition, teamType, freeMods, mods, seed);
    }

    // ---- private ----

    private static void WriteMatchComposite(BanchoPacketWriter w, Match match, bool sendPassword)
    {
        w.WriteUInt16(match.Id);
        w.WriteByte(match.InProgress ? (byte)1 : (byte)0);
        w.WriteByte(0); // powerplay/mods-type byte
        w.WriteUInt32(match.Mods);
        w.WriteString(match.Name);
        w.WriteString(sendPassword ? match.Password : string.Empty);
        w.WriteString(match.MapName);
        w.WriteInt32(match.MapId);
        w.WriteString(match.MapMd5);

        foreach (var s in match.Slots) w.WriteByte((byte)s.Status);
        foreach (var s in match.Slots) w.WriteByte(s.Team);
        foreach (var s in match.Slots)
            if (s.HasPlayer) w.WriteInt32(s.UserId);

        w.WriteInt32(match.HostUserId);
        w.WriteByte(match.Mode);
        w.WriteByte((byte)match.WinCondition);
        w.WriteByte((byte)match.TeamType);
        w.WriteByte(match.FreeMods ? (byte)1 : (byte)0);

        if (match.FreeMods)
            foreach (var s in match.Slots) w.WriteUInt32(s.Mods);

        w.WriteInt32(match.Seed);
    }
}

/// <summary>Parsed arguments from a CREATE_MATCH client packet.</summary>
public sealed record MatchCreateArgs(
    string Name, string Password,
    string MapName, int MapId, string MapMd5,
    byte Mode, MatchWinCondition WinCondition, MatchTeamType TeamType,
    bool FreeMods, uint Mods, int Seed);
