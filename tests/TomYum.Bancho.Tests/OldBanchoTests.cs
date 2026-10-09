using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using TomYum.Bancho;
using Xunit;

namespace TomYum.Bancho.Tests;

/// <summary>
/// Bancho for every old build from b337 (2008) to b20130915.4: what each era is sent and what it
/// sends, checked against the layouts read from their decompiled clients. The readers here follow
/// those layouts (by build number or date), not <see cref="ClientBuild"/>'s table.
/// </summary>
public partial class OldBanchoTests
{
    /// <summary>A build of each era (and the builds within one that know more packets).</summary>
    public static TheoryData<string> Builds() =>
    [
        "b337.test", "b370", "b394a", "b420b", "b452", "b476", "b497", "b504.test", "b534.test", "b558", "b595b",
        "b639", "b699", "b753c", "b833", "b904", "b1077a", "b1122", "b1218", "b1553", "b1596", "b1652", "b1700", "b1704",
        "b1807", "b1814", "b1815", "b1844.test", "b1818", "b1821", "b20120522", "b20120529", "b20120812", "b20120916",
        "b20121003shine.test", "b20121023", "b20121115", "b20121119", "b20121203", "b20121223", "b20130303", "b20130319",
        "b20130329", "b20130513", "b20130603", "b20130606.1", "b20130626", "b20130716", "b20130802.1", "b20130823.3", "b20130915.4",
    ];

    // ---- the login ----

    [Theory]
    [MemberData(nameof(Builds))]
    public void TheLoginBurst_IsInTheBuildsIdsAndOrder(string version)
    {
        var packets = Out(version, LoginBurst());
        var ids = packets.Select(p => (int)p.Id).ToList();

        // The login reply first (a protocol version may come before it from b1807: today's order).
        var reply = packets.First(p => p.Id == 5);
        Assert.Equal(2, BitConverter.ToInt32(reply.Payload));
        if (IsShifted(version))
        {
            Assert.Equal(5, ids[0]);
            var expected = new List<int> { 5 };
            if (Ord(version) >= 497) expected.Add(76);      // protocol version, right after the reply
            if (Ord(version) >= 452) expected.Add(72);      // privileges
            expected.Add(25);                               // the welcome (announce)
            if (Ord(version) >= 370) expected.AddRange([68, 65, 66]); // #osu auto-join + joined, #english
            if (Ord(version) >= 476) expected.Add(73);      // friends
            expected.Add(12);                               // the player's own full user update
            Assert.Equal(expected, ids);
        }
        else
        {
            Assert.Contains(75, ids);
            Assert.Contains(83, ids);
            Assert.Contains(11, ids);
            Assert.DoesNotContain(104, ids);
            // The menu icon: no payload before b20120916 (left out), an image URL until b20121223, then "image|url".
            var icon = packets.Where(p => p.Id == 76).ToList();
            if (Ord(version) < 20120916)
                Assert.Empty(icon);
            else if (Ord(version) < 20121223)
                Assert.Null(OldString(Assert.Single(icon).Payload, 0, out _));
            else
                Assert.Equal("|", OldString(Assert.Single(icon).Payload, 0, out _));
        }
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public void AUsersPresenceAndStats_ReadWholeInTheBuildsLayout(string version)
    {
        var w = new BanchoPacketWriter();
        LoginResponseBuilder.WritePresence(w, Peppy());
        LoginResponseBuilder.WriteStats(w, Peppy());

        var packets = Out(version, w.ToArray());

        if (IsShifted(version))
        {
            var (id, payload) = Assert.Single(packets);
            Assert.Equal(12, id);
            AssertUserUpdate(version, payload);
        }
        else
        {
            AssertPresence(version, Assert.Single(packets, p => p.Id == 83).Payload);
            AssertStats(version, Assert.Single(packets, p => p.Id == 11).Payload);
        }
    }

    [Theory]
    [InlineData("b337.test", -1, -1)]
    [InlineData("b337.test", -2, -2)]
    [InlineData("b337.test", -3, -1)]   // banned came with b639
    [InlineData("b595b", -3, -1)]
    [InlineData("b639", -3, -3)]
    [InlineData("b1122", -5, -1)]
    [InlineData("b1652", -4, -1)]
    [InlineData("b1700", -4, -4)]
    [InlineData("b1704", -5, -1)]
    [InlineData("b1807", -5, -5)]
    [InlineData("b1807", -8, -1)]
    [InlineData("b20121223", -6, -6)]
    [InlineData("b20130915.4", -7, -1)]
    [InlineData("b20130915.4", 7, 7)]
    [InlineData("b20140624", -8, -1)]   // -7 and -8 came with b20151228
    [InlineData("b20151026.3fallback", -8, -1)]
    [InlineData("b20151026.3fallback", -7, -1)]
    [InlineData("b20151228.3", -8, -8)]
    [InlineData("b20151228.3", -7, -7)]
    [InlineData("b20250101", -8, -8)]
    public void LoginRefusals_AreOnesTheBuildKnows(string version, int code, int expected)
    {
        var refusal = LoginResponseBuilder.BuildFailure((LoginFailureReason)code, "Not now.");

        var packets = Out(version, refusal);

        Assert.Equal(expected, BitConverter.ToInt32(Assert.Single(packets, p => p.Id == 5).Payload));
        // The notice: today's 24, one higher (announce) where ids are shifted.
        Assert.Contains(packets, p => p.Id == (IsShifted(version) ? 25 : 24));
    }

    [Theory]
    [InlineData("a\nb\nb337.test|7\n", "b337.test")]
    [InlineData("a\nb\nb1122|7|1\n", "b1122")]
    [InlineData("a\nb\nb20130303|7|1|x:y:z:w:v:|0\n", "b20130303")]
    [InlineData("nonsense", "")]
    public void TheBuild_IsReadFromTheLogin(string body, string version) =>
        Assert.Equal(version, ClientBuild.FromLogin(body).Version);

    [Theory]
    [InlineData("a\nb\nb20140624|7|0|h:a:m:u:d:|0|tomyum-verify\n", true)]
    [InlineData("a\nb\nb20120522|7|0|h:a:m:u:d:|0|tomyum-verify\n", true)]
    [InlineData("a\nb\nb20140624|7|0|h:a:m:u:d:|0|something,TOMYUM-VERIFY\n", true)]
    [InlineData("a\nb\nb20140624|7|0|h:a:m:u:d:|0\n", false)]
    [InlineData("a\nb\nb20140624|7|0|tomyum-verify\n", false)]   // only after the pm field
    public void APatchedBuild_IsAskedToVerifyItsDevice(string body, bool patched)
    {
        var build = ClientBuild.FromLogin(body);

        Assert.Equal(patched, build.HandlesVerification);
        Assert.Equal(patched, build.CanVerifyDevices);
        Assert.True(LoginRequest.TryParse(body, out var request));
        Assert.Equal(patched, request!.HandlesVerification);
        var refusal = LoginResponseBuilder.BuildFailure(LoginFailureReason.VerificationRequired, "Confirm it.");
        var reply = Packets(LegacyPackets.Outgoing(refusal, build));
        Assert.Equal(patched ? -8 : -1, BitConverter.ToInt32(Assert.Single(reply, p => p.Id == 5).Payload));
        Assert.Equal(-1, build.LoginCode(-7));   // only -8 is patched in
    }

    // The login lines of builds before b20120812 as a patched client (step 12) makes them: the install's
    // whole device (osu! md5, adapters, their md5, md5(md5(UninstallID)), disk md5) and the capability, in
    // each era's form (b337 had no city, b1122 to b1652 no client hash, b1700 to b20120529 an incomplete one).
    private const string PatchedDevice =
        "0123456789abcdef0123456789abcdef:001122334455.:11111111111111111111111111111111:22222222222222222222222222222222:33333333333333333333333333333333";

    [Theory]
    [InlineData("b337.test", "0")]   // TCP, first era: today's ids + 1 from 11, -1/-2 only
    [InlineData("b1122", "1")]       // TCP, -1 to -3
    [InlineData("b1218", "0")]
    [InlineData("b1818", "1")]       // today's ids, -1 to -6
    [InlineData("b20120522", "0")]   // sent a 4-part hash unpatched
    public void APatchedOldBuild_SendsItsDeviceAndIsAskedToVerify(string version, string city)
    {
        var body = $"Player\n5f4dcc3b5aa765d61d8327deb882cf99\n{version}|7|{city}|{PatchedDevice}|0|tomyum-verify\n";

        Assert.True(LoginRequest.TryParse(body, out var request));
        Assert.True(request!.HandlesVerification);
        Assert.Equal(version, request.OsuVersion);
        Assert.Equal(city == "1", request.DisplayCity);
        Assert.Equal("22222222222222222222222222222222", request.ClientHashes.UninstallMd5);
        Assert.Equal("33333333333333333333333333333333", request.ClientHashes.DiskSignatureMd5);
        Assert.Equal("001122334455.", request.ClientHashes.AdaptersStr);

        var build = ClientBuild.FromLogin(body);
        Assert.True(build.CanVerifyDevices);
        var refusal = LoginResponseBuilder.BuildFailure(LoginFailureReason.VerificationRequired, "Confirm it.");
        var reply = Packets(LegacyPackets.Outgoing(refusal, build));
        Assert.Equal(-8, BitConverter.ToInt32(Assert.Single(reply, p => p.Id == 5).Payload));
        Assert.Contains(reply, p => p.Id == (IsShifted(version) ? 25 : 24));   // the notice, in the build's own ids

        // The same build unpatched (no device, no capability) is never sent -8: it would read it as a login.
        var unpatched = ClientBuild.FromLogin($"Player\nx\n{version}|7|{city}\n");
        Assert.False(unpatched.CanVerifyDevices);
        Assert.Equal(-1, BitConverter.ToInt32(Assert.Single(Packets(LegacyPackets.Outgoing(refusal, unpatched)), p => p.Id == 5).Payload));
    }

    // ---- chat ----

    [Theory]
    [MemberData(nameof(Builds))]
    public void ChatMessages_ReachTheBuild(string version)
    {
        var w = new BanchoPacketWriter();
        ChatEncoder.WriteMessage(w, new BanchoMessage("peppy", "hello", "#osu", 2));
        ChatEncoder.WriteMessage(w, new BanchoMessage("peppy", "psst", "mind", 2));
        ChatEncoder.WriteMessage(w, new BanchoMessage("peppy", "hi all", "#english", 2));

        var packets = Out(version, w.ToArray());

        // b337 has one chat pane (#osu and private messages); every build reads sender, text and target.
        Assert.Equal(Ord(version) == 337 ? 2 : 3, packets.Count);
        Assert.All(packets, p => Assert.Equal(7, p.Id));
        var at = 0;
        Assert.Equal("peppy", OldString(packets[0].Payload, at, out at));
        Assert.Equal("hello", OldString(packets[0].Payload, at, out at));
        Assert.Equal("#osu", OldString(packets[0].Payload, at, out _));
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public void ChannelPackets_AreTheBuilds(string version)
    {
        var w = new BanchoPacketWriter();
        ChatEncoder.WriteChannelInfo(w, "#english", "English", 3);
        ChatEncoder.WriteChannelJoinSuccess(w, "#english");
        ChatEncoder.WriteChannelJoinSuccess(w, "#multiplayer");
        ChatEncoder.WriteChannelKick(w, "#english");

        var packets = Out(version, w.ToArray());

        if (Ord(version) < 370)
        {
            Assert.Empty(packets); // no channels in b337
            return;
        }
        var shift = IsShifted(version) ? 1 : 0;
        // Room chat opens as an auto-joined tab, then joined.
        Assert.Equal([65 + shift, 64 + shift, 67 + shift, 64 + shift, 66 + shift], packets.Select(p => (int)p.Id));
        Assert.Equal("#english", OldString(packets[1].Payload, 0, out _));
        Assert.Equal("#multiplayer", OldString(packets[2].Payload, 0, out _));
    }

    [Theory]
    [InlineData("b337.test")]
    [InlineData("b370")]
    [InlineData("b1122")]
    [InlineData("b1807")]
    [InlineData("b20121223")]
    public void ChatFromTheBuild_GetsTheSenderIdTodaysClientsAdd(string version)
    {
        var pm = IsShifted(version) ? 26 : 25;
        var w = new BanchoPacketWriter()
            .WritePacket((ServerPacketId)1, x => x.WriteString(null).WriteString("hello").WriteString("#osu"))
            .WritePacket((ServerPacketId)pm, x => x.WriteString(null).WriteString("hey").WriteString("peppy"));

        var packets = In(version, w.ToArray());

        Assert.Equal([1, 25], packets.Select(p => (int)p.Id));
        var r = new BanchoPacketReader(packets[1].Payload);
        Assert.Equal(string.Empty, r.ReadString());
        Assert.Equal("hey", r.ReadString());
        Assert.Equal("peppy", r.ReadString());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void ChatFromBuildsThatSendTheSenderId_PassesAsItIs()
    {
        var w = new BanchoPacketWriter()
            .WritePacket((ServerPacketId)1, x => x.WriteString(null).WriteString("hello").WriteString("#osu").WriteInt32(0));

        var (id, payload) = Assert.Single(In("b20130303", w.ToArray()));

        Assert.Equal(1, id);
        Assert.Equal(w.ToArray()[7..], payload);
    }

    [Theory]
    [InlineData("b370", 64)]
    [InlineData("b639", 79)]
    [InlineData("b1122", 79)]
    [InlineData("b1807", 78)]
    public void ChannelJoinsAndParts_ComeInAsTodays(string version, int id)
    {
        var w = new BanchoPacketWriter().WriteStringPacket((ServerPacketId)id, "#english");

        var (today, payload) = Assert.Single(In(version, w.ToArray()));

        Assert.Equal(id is 64 ? 63 : 78, today);
        Assert.Equal("#english", new BanchoPacketReader(payload).ReadString());
    }

    // ---- statuses ----

    [Fact]
    public void B337Statuses_ComeInAsTodays()
    {
        var w = new BanchoPacketWriter()
            .WritePacket(0, x => x.WriteByte(2).WriteString("Artist - Title").WriteString("abcdef").WriteUInt16(8))
            .WritePacket(0, x => x.WriteByte(7))   // Unknown: nothing more
            .WritePacket(0, x => x.WriteByte(12).WriteString(null).WriteString(null).WriteUInt16(0)) // Lobby
            .WritePacket(0, x => x.WriteByte(10).WriteString(null).WriteString(null).WriteUInt16(0)); // "stats changed"

        var packets = In("b337.test", w.ToArray());

        Assert.Equal([0, 0, 0, 3], packets.Select(p => (int)p.Id));
        AssertTodaysStatus(packets[0].Payload, 2, "Artist - Title", "abcdef", 8, 0, 0);
        AssertTodaysStatus(packets[1].Payload, 7, string.Empty, string.Empty, 0, 0, 0);
        AssertTodaysStatus(packets[2].Payload, 11, string.Empty, string.Empty, 0, 0, 0);
        Assert.Empty(packets[3].Payload); // a request for the player's own stats
    }

    [Theory]
    [InlineData("b370", false)]
    [InlineData("b476", false)]
    [InlineData("b497", true)]
    [InlineData("b1122", true)]
    [InlineData("b1704", true)]
    public void FlaggedStatuses_ComeInAsTodays(string version, bool modeAndBeatmap)
    {
        var w = new BanchoPacketWriter()
            .WritePacket(0, x =>
            {
                x.WriteByte(2).WriteByte(1).WriteString("Artist - Title").WriteString("abcdef").WriteUInt16(8);
                if (modeAndBeatmap)
                    x.WriteByte(1).WriteInt32(123);
            })
            .WritePacket(0, x => x.WriteByte(1).WriteByte(0));

        var packets = In(version, w.ToArray());

        AssertTodaysStatus(packets[0].Payload, 2, "Artist - Title", "abcdef", 8, (byte)(modeAndBeatmap ? 1 : 0), modeAndBeatmap ? 123 : 0);
        AssertTodaysStatus(packets[1].Payload, 1, string.Empty, string.Empty, 0, 0, 0);
    }

    [Theory]
    [InlineData("b1807", false)]
    [InlineData("b20120812", false)]
    [InlineData("b20120916", true)]
    [InlineData("b20130513", true)]
    public void UnflaggedStatuses_ComeInWith32BitMods(string version, bool wide)
    {
        var w = new BanchoPacketWriter().WritePacket(0, x =>
        {
            x.WriteByte(2).WriteString("Artist - Title").WriteString("abcdef");
            if (wide) x.WriteUInt32(0x10048); else x.WriteUInt16(0x48);
            x.WriteByte(1).WriteInt32(123);
        });

        var (id, payload) = Assert.Single(In(version, w.ToArray()));

        Assert.Equal(0, id);
        AssertTodaysStatus(payload, 2, "Artist - Title", "abcdef", wide ? 0x10048u : 0x48u, 1, 123);
    }

    // ---- compression ----

    [Fact]
    public void GzippedPackets_AreUnpacked()
    {
        var message = new BanchoPacketWriter().WriteString(null).WriteString(new string('a', 300)).WriteString("#osu").ToArray();
        var stream = new List<byte>();
        stream.AddRange(Frame(4, 0, []));
        stream.AddRange(Frame(1, 1, Gzip(message)));
        stream.AddRange(Frame(3, 0, []));

        var packets = Packets(LegacyPackets.Decompress([.. stream]));

        Assert.Equal([4, 1, 3], packets.Select(p => (int)p.Id));
        Assert.Equal(message, packets[1].Payload);
    }

    [Fact]
    public void APayloadThatIsntGzip_IsLeftOut_AndUncompressedStreamsPassAsTheyAre()
    {
        byte[] plain = [.. Frame(4, 0, []), .. Frame(3, 0, [])];
        byte[] broken = [.. Frame(1, 1, [1, 2, 3]), .. Frame(4, 0, [])];

        Assert.Same(plain, LegacyPackets.Decompress(plain));
        Assert.Equal([4], Packets(LegacyPackets.Decompress(broken)).Select(p => (int)p.Id));
    }

    // ---- rooms ----

    [Theory]
    [MemberData(nameof(Builds))]
    public void Rooms_ReadWholeInTheBuildsLayout(string version)
    {
        var w = new BanchoPacketWriter().WritePacket(ServerPacketId.UpdateMatch, x => x.WriteRaw(TodaysRoom(id: 7)));

        var (packetId, payload) = Assert.Single(Out(version, w.ToArray()));

        Assert.Equal(IsShifted(version) ? 27 : 26, packetId);
        var r = new ClientReader(payload);
        var v = Ord(version);
        Assert.Equal(7, IsShifted(version) ? r.Byte() : r.UInt16());
        r.Skip(2);
        Assert.Equal(v < 20120916 ? 0x48u : 0x10048u, v < 20120916 ? r.UInt16() : r.UInt32());
        Assert.Equal("room", r.String());
        if (v >= 595)
            Assert.Equal("secret", r.String());
        Assert.Equal("Artist - Title [Hard]", r.String());
        Assert.Equal(123, r.Int32());
        Assert.Equal("abcdef", r.String());
        var statuses = r.Bytes(8);
        Assert.Equal(new byte[] { 4, 8, 1, 2, 2, 2, 2, 2 }, statuses);
        if (v >= 558)
            Assert.Equal(new byte[] { 1, 2, 0, 0, 0, 0, 0, 0 }, r.Bytes(8));
        Assert.Equal(2, r.Int32());
        Assert.Equal(3, r.Int32());
        if (v >= 420)
            Assert.Equal(2, r.Int32()); // host
        if (v >= 476)
            Assert.Equal(v >= 20121003 ? 3 : 0, r.Byte()); // osu!mania only from b20121003
        if (v >= 558)
        {
            Assert.Equal(1, r.Byte());
            Assert.Equal(v >= 1122 ? 2 : 0, r.Byte()); // team vs from b1122
        }
        if (v >= 20130303)
        {
            Assert.Equal(1, r.Byte());
            for (var i = 0; i < 8; i++)
                Assert.Equal(i == 0 ? 8u : 0u, r.UInt32());
        }
        if (v >= 20130513)
            Assert.Equal(1234, r.Int32());
        Assert.Equal(0, r.Remaining);
    }

    [Theory]
    [InlineData("b337.test")]
    [InlineData("b1704")]
    public void RoomsWithIdsPast255_ArentSentWhereIdsAreAByte(string version)
    {
        var w = new BanchoPacketWriter().WritePacket(ServerPacketId.NewMatch, x => x.WriteRaw(TodaysRoom(id: 300)));

        Assert.Empty(Out(version, w.ToArray()));
        Assert.Single(Out("b1807", w.ToArray()));
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public void RoomsFromTheBuild_ComeInAsTodays16SlotRooms(string version)
    {
        var v = Ord(version);
        var w = new BanchoPacketWriter();
        if (IsShifted(version)) w.WriteByte(0); else w.WriteUInt16(0);
        w.WriteByte(0).WriteByte(0);
        if (v < 20120916) w.WriteUInt16(0x48); else w.WriteUInt32(0x10048);
        w.WriteString("my room");
        if (v >= 595) w.WriteString("secret");
        w.WriteString("Artist - Title [Hard]").WriteInt32(123).WriteString("abcdef");
        w.WriteRaw([4, 1, 1, 1, 1, 1, 1, 1]);
        if (v >= 558) w.WriteRaw([1, 0, 0, 0, 0, 0, 0, 0]);
        w.WriteInt32(2);
        if (v >= 420) w.WriteInt32(2);
        if (v >= 476) w.WriteByte(1);
        if (v >= 558) w.WriteByte(1).WriteByte(1);
        if (v >= 20130303) w.WriteByte(0);
        if (v >= 20130513) w.WriteInt32(0);
        var createId = v == 337 ? 32 : IsShifted(version) ? 32 : 31;
        var settingsId = v == 337 ? 51 : IsShifted(version) ? 42 : 41;
        var room = w.ToArray();
        var stream = new BanchoPacketWriter()
            .WritePacket((ServerPacketId)createId, x => x.WriteRaw(room))
            .WritePacket((ServerPacketId)settingsId, x => x.WriteRaw(room));

        var packets = In(version, stream.ToArray());

        Assert.Equal([31, 41], packets.Select(p => (int)p.Id));
        var r = new BanchoPacketReader(packets[0].Payload);
        _ = r.ReadUInt16();
        _ = r.ReadUInt16();
        Assert.Equal(v < 20120916 ? 0x48u : 0x10048u, r.ReadUInt32());
        Assert.Equal("my room", r.ReadString());
        Assert.Equal(v >= 595 ? "secret" : string.Empty, r.ReadString());
        Assert.Equal("Artist - Title [Hard]", r.ReadString());
        Assert.Equal(123, r.ReadInt32());
        Assert.Equal("abcdef", r.ReadString());
        var statuses = r.ReadBytes(16).ToArray();
        Assert.Equal(4, statuses[0]);
        Assert.All(statuses[8..], s => Assert.Equal((byte)SlotStatus.Locked, s));
        Assert.Equal(v >= 558 ? 1 : 0, r.ReadBytes(16)[0]);
        Assert.Equal(2, r.ReadInt32());                        // the player
        Assert.Equal(v >= 420 ? 2 : 0, r.ReadInt32());         // host
        Assert.Equal(v >= 476 ? 1 : 0, r.ReadByte());          // mode
        Assert.Equal(v >= 558 ? 1 : 0, r.ReadByte());          // scoring
        Assert.Equal(v >= 558 ? 1 : 0, r.ReadByte());          // team type
        Assert.Equal(0, r.ReadByte());                         // free mods
        Assert.Equal(0, r.ReadInt32());                        // seed
        Assert.Equal(0, r.Remaining);
    }

    [Theory]
    [InlineData("b337.test", 33, false)]
    [InlineData("b558", 33, false)]
    [InlineData("b595b", 33, true)]
    [InlineData("b1807", 32, true)]
    public void JoiningARoom_ComesInWithAPassword(string version, int id, bool sendsPassword)
    {
        var w = new BanchoPacketWriter().WritePacket((ServerPacketId)id, x =>
        {
            x.WriteInt32(7);
            if (sendsPassword) x.WriteString("secret");
        });

        var (today, payload) = Assert.Single(In(version, w.ToArray()));

        Assert.Equal(32, today);
        var r = new BanchoPacketReader(payload);
        Assert.Equal(7, r.ReadInt32());
        Assert.Equal(sendsPassword ? "secret" : string.Empty, r.ReadString());
    }

    [Theory]
    [InlineData("b337.test", 60, 59)]
    [InlineData("b370", 59, 0)]
    [InlineData("b1122", 59, 82)]
    [InlineData("b1553", 59, 82)]
    public void MatchPackets_UseTheBuildsIds(string version, int complete, int skipped)
    {
        var w = new BanchoPacketWriter()
            .WritePacket(ServerPacketId.MatchComplete, null)
            .WriteInt32Packet(ServerPacketId.MatchPlayerSkipped, 1)
            .WritePacket(ServerPacketId.MatchTransferHost, null);

        var ids = Out(version, w.ToArray()).Select(p => (int)p.Id).ToList();

        Assert.Equal(complete, ids[0]);
        if (version == "b337.test")
            Assert.Equal([60, 52], ids); // no skips, and today's ids + 2 from 51
        else if (Ord(version) < 1553)
            Assert.Equal([complete, 51], ids);
        else
            Assert.Equal([complete, skipped, 51], ids);
    }

    [Fact]
    public void B337sOwnMatchIds_ComeInAsTodays()
    {
        var w = new BanchoPacketWriter()
            .WriteInt32Packet((ServerPacketId)53, 8)        // change mods
            .WritePacket((ServerPacketId)54, null)          // load complete
            .WritePacket((ServerPacketId)58, null)          // failed
            .WritePacket((ServerPacketId)50, null)          // complete (today's + 1)
            .WritePacket((ServerPacketId)42, null);         // never sent: left out

        Assert.Equal([51, 52, 56, 49], In("b337.test", w.ToArray()).Select(p => (int)p.Id));
    }

    // ---- frames ----

    [Theory]
    [MemberData(nameof(Builds))]
    public void SpectatorFrames_HaveTheLeadingIntOnlyFromB20130513(string version)
    {
        var bundle = new BanchoPacketWriter().WriteInt32(99).WriteUInt16(0).WriteByte(0).ToArray();
        var w = new BanchoPacketWriter().WritePacket(ServerPacketId.SpectateFrames, x => x.WriteRaw(bundle));

        var (id, payload) = Assert.Single(Out(version, w.ToArray()));

        Assert.Equal(IsShifted(version) ? 16 : 15, id);
        Assert.Equal(Ord(version) >= 20130513 ? bundle : bundle[4..], payload);
    }

    [Theory]
    [InlineData("b504.test", 19, 27)]
    [InlineData("b1122", 19, 28)]
    [InlineData("b1807", 18, 28)]
    [InlineData("b20130329", 18, 28)]
    public void SpectatorFramesFromTheBuild_GetTheLeadingInt_AndATaggedScoreFrame(string version, int id, int scoreFrame)
    {
        var bundle = new BanchoPacketWriter().WriteUInt16(1).WriteRaw(new byte[14]).WriteByte(0).WriteRaw(new byte[scoreFrame]).ToArray();
        var w = new BanchoPacketWriter().WritePacket((ServerPacketId)id, x => x.WriteRaw(bundle));

        var (today, payload) = Assert.Single(In(version, w.ToArray()));

        Assert.Equal(18, today);
        Assert.Equal(4 + 2 + 14 + 1 + 28, payload.Length);
        Assert.Equal(bundle[..17], payload[4..21]);
    }

    [Theory]
    [InlineData("b337.test", 27)]
    [InlineData("b534.test", 27)]
    [InlineData("b558", 28)]
    public void MatchScoreFrames_GetTheTagByte(string version, int length)
    {
        var w = new BanchoPacketWriter().WritePacket((ServerPacketId)48, x => x.WriteRaw(new byte[length]));

        var (today, payload) = Assert.Single(In(version, w.ToArray()));

        Assert.Equal(47, today);
        Assert.Equal(28, payload.Length);
    }

    // ---- the rest ----

    [Theory]
    [InlineData("b337.test", 0)]
    [InlineData("b370", 1)]
    [InlineData("b420b", 1)]
    [InlineData("b504.test", 3)]
    [InlineData("b1122", 3)]
    [InlineData("b1807", 3)]
    [InlineData("b20121115", 4)]
    public void BeatmapInfo_HasTheBuildsGrades(string version, int grades)
    {
        var w = new BanchoPacketWriter().WritePacket(ServerPacketId.BeatmapInfoReply, x => x
            .WriteInt32(1).WriteInt16(0).WriteInt32(10).WriteInt32(20).WriteInt32(0).WriteByte(2).WriteRaw([3, 4, 5, 6]).WriteString("abcdef"));

        var packets = Out(version, w.ToArray());

        if (grades == 0)
        {
            Assert.Empty(packets);
            return;
        }
        var r = new ClientReader(Assert.Single(packets).Payload);
        Assert.Equal(1, r.Int32());
        r.Skip(2 + 12);
        Assert.Equal(Ord(version) < 420 ? 1 : 2, r.Byte()); // b370's status is "ranked or not"
        Assert.Equal(new byte[] { 3, 4, 5, 6 }[..grades], r.Bytes(grades));
        Assert.Equal("abcdef", r.String());
        Assert.Equal(0, r.Remaining);
    }

    [Theory]
    [InlineData("b420b", null, null)]
    [InlineData("b452", 0x7, null)]
    [InlineData("b476", 0x7, 32)]
    [InlineData("b1704", 0x7, 32)]
    [InlineData("b1807", 0xF, 16)]
    [InlineData("b20120916", 0x3F, 16)]
    public void PrivilegesAndFriends_AreInTheBuildsLayout(string version, int? privileges, int? friendCount)
    {
        var w = new BanchoPacketWriter()
            .WriteInt32Packet(ServerPacketId.Privileges, 0x3F)
            .WritePacket(ServerPacketId.FriendsList, x => x.WriteInt32List([4, 9]));

        var packets = Out(version, w.ToArray());
        var shift = IsShifted(version) ? 1 : 0;

        var sentPrivileges = packets.Where(p => p.Id == 71 + shift).ToList();
        var sentFriends = packets.Where(p => p.Id == 72 + shift).ToList();
        if (privileges is null)
            Assert.Empty(sentPrivileges);
        else
            Assert.Equal(privileges, BitConverter.ToInt32(Assert.Single(sentPrivileges).Payload));
        if (friendCount is null)
        {
            Assert.Empty(sentFriends);
            return;
        }
        var friends = Assert.Single(sentFriends).Payload;
        Assert.Equal(friendCount == 32 ? 4 + 8 : 2 + 8, friends.Length);
        Assert.Equal(2, friendCount == 32 ? BitConverter.ToInt32(friends) : BitConverter.ToInt16(friends));
    }

    [Fact]
    public void B337_IsToldAUserLeft_WithAWholeUserUpdate()
    {
        var w = new BanchoPacketWriter().WritePacket(ServerPacketId.UserLogout, x => x.WriteInt32(5).WriteByte(0));

        var (id, payload) = Assert.Single(Out("b337.test", w.ToArray()));

        Assert.Equal(13, id);
        Assert.Equal(new byte[] { 5, 0, 0, 0, 0, 7 }, payload);
    }

    [Fact]
    public void TodaysClients_AreLeftAlone()
    {
        var stream = LoginBurst();

        Assert.Same(stream, LegacyPackets.Outgoing(stream, ClientBuild.Parse("b20250101")));
        Assert.Same(stream, LegacyPackets.Incoming(stream, ClientBuild.Parse("b20250101")));
    }

    // ---- what the builds read (from the decompiled clients) ----

    /// <summary>b337-b1704's user update (12), read as the build reads it.</summary>
    private static void AssertUserUpdate(string version, byte[] payload)
    {
        var v = Ord(version);
        var r = new ClientReader(payload);
        Assert.Equal(2, r.Int32());
        if (v < 370)
        {
            Assert.Equal(1, r.Byte()); // new stats
            Assert.Equal("peppy", r.String());
            AssertStatistics(ref r, wideRank: true);
            Assert.Equal("2.png", r.String());
            Assert.Equal(7 + 24, r.Byte());
            Assert.Equal("TH", r.String());
            Assert.Equal(2, r.Byte());           // playing
            Assert.Equal("Artist - Title [Hard]", r.String());
            Assert.Equal("abcdef", r.String());
            Assert.Equal(72, r.UInt16());
            Assert.Equal(0, r.Remaining);
            return;
        }

        Assert.Equal(2, r.Byte());               // full
        Assert.Equal(2, r.Byte());               // playing
        Assert.Equal(1, r.Byte());               // with a beatmap
        Assert.Equal("Artist - Title [Hard]", r.String());
        Assert.Equal("abcdef", r.String());
        Assert.Equal(72, r.UInt16());
        if (v >= 497)
        {
            Assert.Equal(0, r.Byte());           // mania (3) isn't a mode there yet
            Assert.Equal(123, r.Int32());
        }
        AssertStatistics(ref r, wideRank: v >= 699);
        Assert.Equal("peppy", r.String());
        Assert.Equal("2.png", r.String());
        Assert.Equal(7 + 24, r.Byte());
        Assert.Equal("TH", r.String());
        if (v >= 476)
            Assert.Equal(5, r.Byte());           // normal + supporter
        if (v >= 1218)
        {
            Assert.Equal(100.5f, r.Single());
            Assert.Equal(13.75f, r.Single());
        }
        Assert.Equal(0, r.Remaining);
    }

    private static void AssertStatistics(ref ClientReader r, bool wideRank)
    {
        Assert.Equal(1_000_000, r.Int64());
        Assert.Equal(0.98f, r.Single());
        Assert.Equal(42, r.Int32());
        Assert.Equal(5_000_000, r.Int64());
        Assert.Equal(3, wideRank ? r.Int32() : r.UInt16());
    }

    /// <summary>b1807 on: USER_STATS (11).</summary>
    private static void AssertStats(string version, byte[] payload)
    {
        var v = Ord(version);
        var r = new ClientReader(payload);
        Assert.Equal(2, r.Int32());
        Assert.Equal(2, r.Byte());
        Assert.Equal("Artist - Title [Hard]", r.String());
        Assert.Equal("abcdef", r.String());
        Assert.Equal(v < 20120916 ? 72u : 0x10048u, v < 20120916 ? r.UInt16() : r.UInt32());
        Assert.Equal(v >= 20121003 ? 3 : 0, r.Byte());
        Assert.Equal(123, r.Int32());
        AssertStatistics(ref r, wideRank: true);
        if (v >= 20120812)
            Assert.Equal(4321, r.Int16());
        Assert.Equal(0, r.Remaining);
    }

    /// <summary>b1807 on: USER_PRESENCE (83), the old layout until b20121203.</summary>
    private static void AssertPresence(string version, byte[] payload)
    {
        var v = Ord(version);
        var r = new ClientReader(payload);
        Assert.Equal(2, r.Int32());
        Assert.Equal("peppy", r.String());
        if (v >= 20121203)
        {
            Assert.Equal(7 + 24, r.Byte());
            Assert.Equal(CountryCodes.ToNumeric("th"), r.Byte());
            Assert.Equal(5 | (3 << 5), r.Byte());
        }
        else
        {
            Assert.Equal(1, r.Byte());           // avatar: png
            Assert.Equal(7 + 24, r.Byte());
            Assert.Equal(CountryCodes.ToNumeric("th"), r.Byte());
            Assert.Equal("TH", r.String());      // location
            Assert.Equal(5, r.Byte());           // permissions
        }
        Assert.Equal(100.5f, r.Single());
        Assert.Equal(13.75f, r.Single());
        Assert.Equal(3, r.Int32());
        if (v is >= 20120812 and < 20121203)
            Assert.Equal(v >= 20121003 ? 3 : 0, r.Byte());
        Assert.Equal(0, r.Remaining);
    }

    private static void AssertTodaysStatus(byte[] payload, byte action, string text, string md5, uint mods, byte mode, int beatmapId)
    {
        var r = new BanchoPacketReader(payload);
        Assert.Equal(action, r.ReadByte());
        Assert.Equal(text, r.ReadString());
        Assert.Equal(md5, r.ReadString());
        Assert.Equal(mods, r.ReadUInt32());
        Assert.Equal(mode, r.ReadByte());
        Assert.Equal(beatmapId, r.ReadInt32());
        Assert.Equal(0, r.Remaining);
    }

    // ---- helpers ----

    /// <summary>A build's place in time: its number (b337 → 337), or its date (b20120812 → 20120812).</summary>
    private static long Ord(string version) => long.Parse(Leading().Match(version).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsShifted(string version) => Ord(version) < 1807;

    [GeneratedRegex(@"^b(\d+)")]
    private static partial Regex Leading();

    private static BanchoPresence Peppy() => new()
    {
        UserId = 2,
        Username = "peppy",
        UtcOffset = 7,
        CountryCode = CountryCodes.ToNumeric("th"),
        Privileges = (BanchoPrivileges)5,
        Action = 2,
        ActionInfo = "Artist - Title [Hard]",
        BeatmapMd5 = "abcdef",
        Mods = (Mods)0x10048, // a bit past 16
        Mode = 3,
        BeatmapId = 123,
        RankedScore = 1_000_000,
        Accuracy = 0.98f,
        PlayCount = 42,
        TotalScore = 5_000_000,
        GlobalRank = 3,
        Performance = 4321,
        Longitude = 100.5f,
        Latitude = 13.75f,
    };

    private static byte[] LoginBurst() => LoginResponseBuilder.BuildSuccess(Peppy(), "Welcome!", w =>
    {
        ChatEncoder.WriteChannelAutoJoin(w, "#osu", "Main", 1);
        ChatEncoder.WriteChannelJoinSuccess(w, "#osu");
        ChatEncoder.WriteChannelInfo(w, "#english", "English", 0);
    }, friends: [4, 9]);

    /// <summary>Today's room (16 slots): slot 1 player 2 (not ready, team 1), slot 2 player 3 (ready, team 2), slot 3 open.</summary>
    private static byte[] TodaysRoom(ushort id)
    {
        var w = new BanchoPacketWriter()
            .WriteUInt16(id).WriteByte(0).WriteByte(0).WriteUInt32(0x10048)
            .WriteString("room").WriteString("secret").WriteString("Artist - Title [Hard]").WriteInt32(123).WriteString("abcdef");
        for (var i = 0; i < 16; i++) w.WriteByte(i switch { 0 => 4, 1 => 8, 2 => 1, _ => 2 });
        for (var i = 0; i < 16; i++) w.WriteByte(i switch { 0 => 1, 1 => 2, _ => 0 });
        w.WriteInt32(2).WriteInt32(3);
        w.WriteInt32(2).WriteByte(3).WriteByte(1).WriteByte(2).WriteByte(1);
        for (var i = 0; i < 16; i++) w.WriteUInt32(i == 0 ? 8u : 0u);
        return w.WriteInt32(1234).ToArray();
    }

    private static List<(ushort Id, byte[] Payload)> Out(string version, byte[] stream) =>
        Packets(LegacyPackets.Outgoing(stream, ClientBuild.Parse(version)));

    private static List<(ushort Id, byte[] Payload)> In(string version, byte[] stream) =>
        Packets(LegacyPackets.Incoming(stream, ClientBuild.Parse(version)));

    private static List<(ushort Id, byte[] Payload)> Packets(byte[] stream)
    {
        var list = new List<(ushort, byte[])>();
        var r = new BanchoPacketReader(stream);
        while (r.CanReadHeader)
        {
            var id = r.ReadUInt16();
            _ = r.ReadByte();
            var length = (int)r.ReadUInt32();
            list.Add((id, r.ReadBytes(length).ToArray()));
        }
        Assert.Equal(0, r.Remaining);
        return list;
    }

    private static byte[] Frame(ushort id, byte compressed, byte[] payload)
    {
        var frame = new byte[7 + payload.Length];
        BitConverter.GetBytes(id).CopyTo(frame, 0);
        frame[2] = compressed;
        BitConverter.GetBytes(payload.Length).CopyTo(frame, 3);
        payload.CopyTo(frame, 7);
        return frame;
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            gzip.Write(data);
        return output.ToArray();
    }

    /// <summary>A string as the old builds read one: 0x00 is null; otherwise a marker, a 7-bit length and UTF-8.</summary>
    private static string? OldString(byte[] payload, int at, out int next)
    {
        var r = new ClientReader(payload);
        r.Skip(at);
        var text = r.String();
        next = payload.Length - r.Remaining;
        return text;
    }

    /// <summary>Reads like the old builds do (and fails the test when a payload runs short, where they'd disconnect).</summary>
    private ref struct ClientReader(byte[] payload)
    {
        private readonly byte[] _payload = payload;
        private int _at;

        public readonly int Remaining => _payload.Length - _at;

        public void Skip(int count) => Take(count);

        public byte Byte() => Take(1)[0];

        public ushort UInt16() => BitConverter.ToUInt16(Take(2));

        public short Int16() => BitConverter.ToInt16(Take(2));

        public int Int32() => BitConverter.ToInt32(Take(4));

        public uint UInt32() => BitConverter.ToUInt32(Take(4));

        public long Int64() => BitConverter.ToInt64(Take(8));

        public float Single() => BitConverter.ToSingle(Take(4));

        public byte[] Bytes(int count) => Take(count).ToArray();

        public string? String()
        {
            if (Byte() == 0)
                return null;
            int length = 0, shift = 0;
            byte b;
            do
            {
                b = Byte();
                length |= (b & 0x7F) << shift;
                shift += 7;
            }
            while ((b & 0x80) != 0);
            return Encoding.UTF8.GetString(Take(length));
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            Assert.True(Remaining >= count, "The build would read past the end of the packet (and disconnect).");
            var span = _payload.AsSpan(_at, count);
            _at += count;
            return span;
        }
    }
}
