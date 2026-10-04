namespace TomYum.Bancho;

/// <summary>
/// A multiplayer room (the match composite) in the layout of a build before b20130513, which
/// differs from today's in the middle, not just at the end (read from the decompiled clients):
/// <code>
/// id            u8 before b1807, else u16
/// in progress   u8, match type u8
/// mods          16-bit before b20120916, else 32-bit
/// name, [password from b595b], beatmap name, beatmap id, beatmap checksum
/// 8 slot statuses, [8 slot teams from b558]
/// the user id of each slot with a player
/// [host from b420], [play mode from b476], [scoring and team type from b558]
/// [free mods (and 8 slot mods when on) from b20130303]
/// </code>
/// (From b20130513 it's today's room with 8 slots, and from b20140528 with 16.) These layouts
/// assume protocol version 19, which these builds are sent at login: some fields depend on it.
/// </summary>
internal sealed class LegacyRoom
{
    private const int OldSlots = 8;

    private ushort _id;
    private byte _inProgress;
    private byte _type;
    private uint _mods;
    private string _name = string.Empty;
    private string _password = string.Empty;
    private string _mapName = string.Empty;
    private int _mapId;
    private string _mapMd5 = string.Empty;
    private byte[] _statuses = [];
    private byte[] _teams = [];
    private int[] _players = [];
    private int _host;
    private byte _mode;
    private byte _scoring;
    private byte _teamType;
    private byte _freeMods;
    private uint[] _slotMods = [];
    private int _seed;

    /// <summary>A server room in today's layout with <paramref name="slots"/> slots; null when it isn't one.</summary>
    public static LegacyRoom? ReadToday(ReadOnlySpan<byte> payload, int slots)
    {
        try
        {
            var r = new BanchoPacketReader(payload);
            var room = new LegacyRoom
            {
                _id = r.ReadUInt16(),
                _inProgress = r.ReadByte(),
                _type = r.ReadByte(),
                _mods = r.ReadUInt32(),
                _name = r.ReadString(),
                _password = r.ReadString(),
                _mapName = r.ReadString(),
                _mapId = r.ReadInt32(),
                _mapMd5 = r.ReadString(),
            };
            room.ReadSlots(ref r, slots, teams: true);
            room._host = r.ReadInt32();
            room._mode = r.ReadByte();
            room._scoring = r.ReadByte();
            room._teamType = r.ReadByte();
            room._freeMods = r.ReadByte();
            room._slotMods = new uint[slots];
            if (room._freeMods != 0)
                for (var i = 0; i < slots; i++) room._slotMods[i] = r.ReadUInt32();
            room._seed = r.ReadInt32();
            return room;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>A room <paramref name="build"/> sent (creating one, or its settings); null when it isn't one.</summary>
    public static LegacyRoom? ReadFrom(ReadOnlySpan<byte> payload, ClientBuild build)
    {
        try
        {
            var r = new BanchoPacketReader(payload);
            var room = new LegacyRoom
            {
                _id = build.Has(BanchoFeature.TodaysIds) ? r.ReadUInt16() : r.ReadByte(),
                _inProgress = r.ReadByte(),
                _type = r.ReadByte(),
                _mods = build.Has(BanchoFeature.WideMods) ? r.ReadUInt32() : r.ReadUInt16(),
                _name = r.ReadString(),
                _password = build.Has(BanchoFeature.RoomPassword) ? r.ReadString() : string.Empty,
                _mapName = r.ReadString(),
                _mapId = r.ReadInt32(),
                _mapMd5 = r.ReadString(),
            };
            room.ReadSlots(ref r, OldSlots, teams: build.Has(BanchoFeature.TaggedScoreFrames));
            room._slotMods = new uint[OldSlots];
            // The rest is read only where the build writes it (and b420-b452 builds read the host only when it's there).
            if (build.Has(BanchoFeature.RoomHost) && r.Remaining >= 4)
                room._host = r.ReadInt32();
            if (build.Has(BanchoFeature.Friends) && r.Remaining >= 1)
                room._mode = r.ReadByte();
            if (build.Has(BanchoFeature.TaggedScoreFrames) && r.Remaining >= 2)
            {
                room._scoring = r.ReadByte();
                room._teamType = r.ReadByte();
            }
            if (build.Has(BanchoFeature.MessageSenderId) && r.Remaining >= 1)
            {
                room._freeMods = r.ReadByte();
                if (room._freeMods != 0)
                    for (var i = 0; i < OldSlots; i++) room._slotMods[i] = r.ReadUInt32();
            }
            if (build.Has(BanchoFeature.FrameBundleExtra) && r.Remaining >= 4)
                room._seed = r.ReadInt32();
            return room;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// The room as <paramref name="build"/> reads it (its first 8 slots); null when the build can't
    /// show it (an id past 255 where ids are a byte).
    /// </summary>
    public byte[]? WriteFor(ClientBuild build)
    {
        var w = new BanchoPacketWriter(256);
        if (build.Has(BanchoFeature.TodaysIds))
            w.WriteUInt16(_id);
        else if (_id <= byte.MaxValue)
            w.WriteByte((byte)_id);
        else
            return null;
        w.WriteByte(_inProgress);
        w.WriteByte(_type);
        if (build.Has(BanchoFeature.WideMods))
            w.WriteUInt32(_mods);
        else
            w.WriteUInt16((ushort)_mods);
        LegacyLayouts.WriteText(w, _name);
        if (build.Has(BanchoFeature.RoomPassword))
            w.WriteString(_password); // as today: none is 0x00 (null there), which isn't a password
        LegacyLayouts.WriteText(w, _mapName);
        w.WriteInt32(_mapId);
        LegacyLayouts.WriteText(w, _mapMd5);

        // Quit (128) came with b1122; older builds see the player's other flags.
        var statusMask = build.Has(BanchoFeature.TeamVs) ? 0xFF : 0x7F;
        var statuses = new byte[OldSlots];
        for (var i = 0; i < OldSlots; i++)
            statuses[i] = i < _statuses.Length ? (byte)(_statuses[i] & statusMask) : (byte)SlotStatus.Locked;
        w.WriteRaw(statuses);
        if (build.Has(BanchoFeature.TaggedScoreFrames))
            for (var i = 0; i < OldSlots; i++) w.WriteByte(i < _teams.Length ? _teams[i] : (byte)0);
        for (var i = 0; i < OldSlots; i++)
            if (((SlotStatus)statuses[i] & SlotStatus.HasPlayer) != 0)
                w.WriteInt32(_players[i]);

        if (build.Has(BanchoFeature.RoomHost))
            w.WriteInt32(_host);
        if (build.Has(BanchoFeature.Friends))
            w.WriteByte(LegacyLayouts.Mode(_mode, build));
        if (build.Has(BanchoFeature.TaggedScoreFrames))
        {
            w.WriteByte(_scoring);
            // Team vs and tag team vs came with b1122.
            w.WriteByte(build.Has(BanchoFeature.TeamVs) || _teamType <= 1 ? _teamType : (byte)0);
        }
        if (build.Has(BanchoFeature.MessageSenderId))
        {
            w.WriteByte(_freeMods);
            if (_freeMods != 0)
                for (var i = 0; i < OldSlots; i++) w.WriteUInt32(i < _slotMods.Length ? _slotMods[i] : 0);
        }
        if (build.Has(BanchoFeature.FrameBundleExtra))
            w.WriteInt32(_seed);
        return w.ToArray();
    }

    /// <summary>The room in today's layout with <paramref name="slots"/> slots (the ones past 8 locked), for the handlers.</summary>
    public byte[] WriteToday(int slots)
    {
        var w = new BanchoPacketWriter(256);
        w.WriteUInt16(_id);
        w.WriteByte(_inProgress);
        w.WriteByte(_type);
        w.WriteUInt32(_mods);
        w.WriteString(_name);
        w.WriteString(_password);
        w.WriteString(_mapName);
        w.WriteInt32(_mapId);
        w.WriteString(_mapMd5);
        for (var i = 0; i < slots; i++) w.WriteByte(i < _statuses.Length ? _statuses[i] : (byte)SlotStatus.Locked);
        for (var i = 0; i < slots; i++) w.WriteByte(i < _teams.Length ? _teams[i] : (byte)0);
        for (var i = 0; i < Math.Min(slots, _statuses.Length); i++)
            if (((SlotStatus)_statuses[i] & SlotStatus.HasPlayer) != 0)
                w.WriteInt32(_players[i]);
        w.WriteInt32(_host);
        w.WriteByte(_mode);
        w.WriteByte(_scoring);
        w.WriteByte(_teamType);
        w.WriteByte(_freeMods);
        if (_freeMods != 0)
            for (var i = 0; i < slots; i++) w.WriteUInt32(i < _slotMods.Length ? _slotMods[i] : 0);
        w.WriteInt32(_seed);
        return w.ToArray();
    }

    private void ReadSlots(ref BanchoPacketReader r, int slots, bool teams)
    {
        _statuses = new byte[slots];
        _teams = new byte[slots];
        _players = new int[slots];
        for (var i = 0; i < slots; i++) _statuses[i] = r.ReadByte();
        if (teams)
            for (var i = 0; i < slots; i++) _teams[i] = r.ReadByte();
        for (var i = 0; i < slots; i++)
            if (((SlotStatus)_statuses[i] & SlotStatus.HasPlayer) != 0)
                _players[i] = r.ReadInt32();
    }
}
