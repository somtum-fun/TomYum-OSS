using System.Buffers.Binary;
using System.Globalization;

namespace TomYum.Bancho;

/// <summary>
/// Payloads in old builds' layouts (b337 to b20130915.4, read from their decompiled clients), shared
/// by <see cref="EarlyPackets"/> (shifted ids) and <see cref="LegacyPackets"/> (today's ids).
/// </summary>
internal static class LegacyLayouts
{
    /// <summary>A score frame before b558: 27 bytes, without the tag byte.</summary>
    public const int UntaggedScoreFrameLength = LegacyFrames.OldScoreFrameLength - 1;

    /// <summary>
    /// A string as old builds write one: an empty string is <c>0x0b 0x00</c>. A bare <c>0x00</c>
    /// (how an empty string is written today) reads as null there, which they don't expect.
    /// </summary>
    public static void WriteText(BanchoPacketWriter w, string? text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty);
        w.WriteByte(0x0b).WriteUleb128((uint)bytes.Length).WriteRaw(bytes);
    }

    /// <summary>Today's status (action) as <paramref name="build"/> numbers them.</summary>
    public static byte Action(byte action, ClientBuild build)
    {
        if (action > 13)
            return 0; // newer than any of these builds
        if (!build.Has(BanchoFeature.CompletenessUserUpdate))
        {
            // b337: 10 is "stats changed", then Paused, Lobby; no Multiplaying or osu!direct.
            return action switch
            {
                10 => 11,
                11 => 12,
                12 => 5,
                13 => 0,
                _ => action,
            };
        }
        return action == 13 && !build.Has(BanchoFeature.Friends) ? (byte)0 : action;
    }

    /// <summary>A play mode <paramref name="build"/> knows: osu!mania came with b20121003.</summary>
    public static byte Mode(byte mode, ClientBuild build) => mode > 3 || (mode == 3 && !build.Has(BanchoFeature.Mania)) ? (byte)0 : mode;

    /// <summary>
    /// A status the build sent (CHANGE_ACTION 0) as today's (action, text, md5, 32-bit mods, mode,
    /// beatmap id). Null when it isn't one, or for b337's "stats changed" status (10), which isn't an action:
    /// <paramref name="statsPing"/> is then true.
    /// </summary>
    public static byte[]? Status(byte[] payload, ClientBuild build, out bool statsPing)
    {
        statsPing = false;
        try
        {
            var r = new BanchoPacketReader(payload);
            var action = r.ReadByte();
            string text = string.Empty, md5 = string.Empty;
            uint mods = 0;
            byte mode = 0;
            var beatmapId = 0;
            if (build.Has(BanchoFeature.TodaysIds))
            {
                // b1807 on: no has-beatmap flag; mods are 16-bit before b20120916.
                text = r.ReadString();
                md5 = r.ReadString();
                mods = build.Has(BanchoFeature.WideMods) ? r.ReadUInt32() : r.ReadUInt16();
                mode = r.ReadByte();
                beatmapId = r.ReadInt32();
            }
            else
            {
                // b337: the beatmap part unless the status is Unknown (7); later, behind a flag.
                var hasBeatmap = build.IsFirstEra ? action != 7 : r.ReadByte() != 0;
                if (hasBeatmap)
                {
                    text = r.ReadString();
                    md5 = r.ReadString();
                    mods = r.ReadUInt16();
                    // Mode and beatmap id from b497 (with the protocol version it's sent).
                    if (r.Remaining >= 5)
                    {
                        mode = r.ReadByte();
                        beatmapId = r.ReadInt32();
                    }
                }
                if (build.IsFirstEra)
                {
                    if (action == 10)
                    {
                        statsPing = true;
                        return null;
                    }
                    action = action switch
                    {
                        11 => 10,
                        12 => 11,
                        _ => action,
                    };
                }
            }
            return new BanchoPacketWriter(payload.Length + 8)
                .WriteByte(action).WriteString(text).WriteString(md5).WriteUInt32(mods).WriteByte(mode).WriteInt32(beatmapId)
                .ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// A spectator frame bundle as <paramref name="build"/> reads it: without today's leading int
    /// before b20130513 (today's extras at the end are left: they're read past).
    /// </summary>
    public static byte[] FramesOut(byte[] payload, ClientBuild build) =>
        build.Has(BanchoFeature.FrameBundleExtra) || payload.Length < 4 ? payload : payload[4..];

    /// <summary>
    /// A spectator frame bundle <paramref name="build"/> sent, as today's handler reads it: the leading
    /// int added before b20130513, and the tag byte on a b337-b534 score frame (27 bytes).
    /// </summary>
    public static byte[] FramesIn(byte[] payload, ClientBuild build)
    {
        if (build.Has(BanchoFeature.FrameBundleExtra))
            return payload;
        var tag = false;
        if (!build.Has(BanchoFeature.TaggedScoreFrames) && payload.Length >= 2)
        {
            // count (u16), 14-byte frames, action (u8), then the score frame (optional).
            var count = BinaryPrimitives.ReadUInt16LittleEndian(payload);
            tag = payload.Length == 2 + (count * LegacyFrames.ReplayFrameLength) + 1 + UntaggedScoreFrameLength;
        }
        var upgraded = new byte[4 + payload.Length + (tag ? 1 : 0)];
        payload.CopyTo(upgraded, 4); // extra 0, then the bundle (and a 0 tag)
        return upgraded;
    }

    /// <summary>A match score frame <paramref name="build"/> sent, with the tag byte b337-b534 don't send.</summary>
    public static byte[] ScoreFrameIn(byte[] payload, ClientBuild build)
    {
        if (build.Has(BanchoFeature.TaggedScoreFrames) || payload.Length != UntaggedScoreFrameLength)
            return payload;
        var tagged = new byte[LegacyFrames.OldScoreFrameLength];
        payload.CopyTo(tagged, 0);
        return tagged;
    }

    /// <summary>A message the build sent (sender, text, target) with the sender id today's add (0).</summary>
    public static byte[] MessageIn(byte[] payload, ClientBuild build) =>
        build.Has(BanchoFeature.MessageSenderId) ? payload : [.. payload, 0, 0, 0, 0];

    /// <summary>
    /// A BEATMAP_INFO_REPLY as <paramref name="build"/> reads it: today's four grades (osu!, catch,
    /// taiko, mania) cut to three before b20121115 and to one (osu!) before b504; b370's status is a bool.
    /// Null when it isn't one.
    /// </summary>
    public static byte[]? BeatmapInfo(byte[] payload, ClientBuild build)
    {
        var grades = build.Has(BanchoFeature.FourGrades) ? 4 : build.Has(BanchoFeature.ThreeGrades) ? 3 : 1;
        if (grades == 4)
            return payload;
        try
        {
            var r = new BanchoPacketReader(payload);
            var count = r.ReadInt32();
            var w = new BanchoPacketWriter(payload.Length);
            w.WriteInt32(count);
            for (var i = 0; i < count; i++)
            {
                w.WriteInt16(r.ReadInt16());   // index
                w.WriteInt32(r.ReadInt32());   // beatmap id
                w.WriteInt32(r.ReadInt32());   // set id
                w.WriteInt32(r.ReadInt32());   // thread id
                var status = r.ReadByte();
                w.WriteByte(build.Has(BanchoFeature.RoomHost) ? status : status == 1 || status == 2 ? (byte)1 : (byte)0);
                var all = r.ReadBytes(4);
                w.WriteRaw(all[..grades]);
                LegacyLayouts.WriteText(w, r.ReadString()); // checksum
            }
            return w.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// Today's USER_STATS for a build before b20121003 that has today's ids: 16-bit mods before
    /// b20120916, no osu!mania, and no pp before b20120812.
    /// </summary>
    public static byte[] StatsFor(Stats stats, ClientBuild build)
    {
        var w = new BanchoPacketWriter(96);
        w.WriteInt32(stats.UserId);
        w.WriteByte(Action(stats.Action, build));
        WriteText(w, stats.ActionText);
        WriteText(w, stats.BeatmapMd5);
        if (build.Has(BanchoFeature.WideMods))
            w.WriteUInt32(stats.Mods);
        else
            w.WriteUInt16((ushort)stats.Mods);
        w.WriteByte(Mode(stats.Mode, build));
        w.WriteInt32(stats.BeatmapId);
        w.WriteInt64(stats.RankedScore);
        w.WriteSingle(stats.Accuracy);
        w.WriteInt32(stats.PlayCount);
        w.WriteInt64(stats.TotalScore);
        w.WriteInt32(stats.Rank);
        if (build.Has(BanchoFeature.Performance))
            w.WriteInt16(stats.Performance);
        return w.ToArray();
    }

    /// <summary>
    /// Today's USER_PRESENCE for b1807 to b20121119: an avatar flag after the name, the location as
    /// text after the country, permissions as a whole byte, and (from b20120812) the play mode at the end.
    /// </summary>
    public static byte[] PresenceFor(Presence presence, ClientBuild build)
    {
        var w = new BanchoPacketWriter(64);
        w.WriteInt32(presence.UserId);
        WriteText(w, presence.Username);
        w.WriteByte(1); // has an avatar (png): <avatar host>/a/<id>_000.png
        w.WriteByte(presence.Timezone);
        w.WriteByte(presence.Country);
        WriteText(w, Location(presence.Country));
        w.WriteByte((byte)(presence.Privileges & build.PrivilegeMask));
        w.WriteSingle(presence.Longitude);
        w.WriteSingle(presence.Latitude);
        w.WriteInt32(presence.Rank);
        if (build.Has(BanchoFeature.Performance))
            w.WriteByte(Mode(presence.Mode, build));
        return w.ToArray();
    }

    /// <summary>The location old builds show for a player: their country's code.</summary>
    public static string Location(byte country) => CountryCodes.ToIso(country) is { Length: > 0 } iso ? iso : "XX";

    /// <summary>The avatar's file name for b337-b1704 (fetched from osu.&lt;domain&gt;/forum/download.php?avatar=&lt;it&gt;).</summary>
    public static string AvatarFile(int userId) => string.Create(CultureInfo.InvariantCulture, $"{userId}.png");

    /// <summary>Today's USER_PRESENCE (83).</summary>
    internal sealed record Presence(int UserId, string Username, byte Timezone, byte Country, int Privileges, byte Mode,
        float Longitude, float Latitude, int Rank)
    {
        public static Presence? Read(byte[] payload)
        {
            try
            {
                var r = new BanchoPacketReader(payload);
                var userId = r.ReadInt32();
                var name = r.ReadString();
                var timezone = r.ReadByte();
                var country = r.ReadByte();
                var privilegesAndMode = r.ReadByte();
                var longitude = r.ReadSingle();
                var latitude = r.ReadSingle();
                var rank = r.ReadInt32();
                return new Presence(userId, name, timezone, country, privilegesAndMode & 0x1F, (byte)(privilegesAndMode >> 5), longitude, latitude, rank);
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException)
            {
                return null;
            }
        }
    }

    /// <summary>Today's USER_STATS (11).</summary>
    internal sealed record Stats(int UserId, byte Action, string ActionText, string BeatmapMd5, uint Mods, byte Mode, int BeatmapId,
        long RankedScore, float Accuracy, int PlayCount, long TotalScore, int Rank, short Performance)
    {
        public static Stats? Read(byte[] payload)
        {
            try
            {
                var r = new BanchoPacketReader(payload);
                return new Stats(r.ReadInt32(), r.ReadByte(), r.ReadString(), r.ReadString(), r.ReadUInt32(), r.ReadByte(), r.ReadInt32(),
                    r.ReadInt64(), r.ReadSingle(), r.ReadInt32(), r.ReadInt64(), r.ReadInt32(), r.Remaining >= 2 ? r.ReadInt16() : (short)0);
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException)
            {
                return null;
            }
        }
    }
}
