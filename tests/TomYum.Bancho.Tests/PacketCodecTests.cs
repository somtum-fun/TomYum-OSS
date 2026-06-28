using TomYum.Bancho;
using Xunit;

namespace TomYum.Bancho.Tests;

/// <summary>
/// Round-trip tests for the hand-rolled reader/writer primitives. These lock the exact
/// wire encoding (little-endian, presence-byte + ULEB128 strings, 7-byte framing) from
/// <c>memories/protocols/bancho-stable.md</c> §2 so future changes can't silently drift.
/// </summary>
public class PacketCodecTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(127u)]
    [InlineData(128u)]
    [InlineData(16384u)]
    [InlineData(uint.MaxValue)]
    public void Uleb128_RoundTrips(uint value)
    {
        var writer = new BanchoPacketWriter(8);
        writer.WriteUleb128(value);

        var reader = new BanchoPacketReader(writer.ToArray());
        Assert.Equal(value, reader.ReadUleb128());
    }

    [Theory]
    [InlineData("")]
    [InlineData("osu!")]
    [InlineData("TomYum サーバー")] // multi-byte UTF-8 must survive the ULEB128 length.
    public void String_RoundTrips(string value)
    {
        var writer = new BanchoPacketWriter(32);
        writer.WriteString(value);

        var reader = new BanchoPacketReader(writer.ToArray());
        Assert.Equal(value, reader.ReadString());
    }

    [Fact]
    public void EmptyString_IsSinglePresenceByte()
    {
        var writer = new BanchoPacketWriter(4);
        writer.WriteString(string.Empty);

        var bytes = writer.ToArray();
        Assert.Equal(new byte[] { 0x00 }, bytes);
    }

    [Fact]
    public void NonEmptyString_HasExpectedLayout()
    {
        var writer = new BanchoPacketWriter(8);
        writer.WriteString("ab");

        // 0x0b marker, ULEB128 length (2), then 'a' 'b'.
        Assert.Equal(new byte[] { 0x0b, 0x02, 0x61, 0x62 }, writer.ToArray());
    }

    [Fact]
    public void Int32Packet_FramesWithSevenByteHeader()
    {
        var writer = new BanchoPacketWriter(16);
        writer.WriteInt32Packet(ServerPacketId.UserId, 1337);

        var bytes = writer.ToArray();

        // id (u16) + pad byte + length (u32) + 4-byte payload = 11 bytes.
        Assert.Equal(PacketHeader.Size + sizeof(int), bytes.Length);

        var reader = new BanchoPacketReader(bytes);
        var header = reader.ReadHeader();
        Assert.Equal(ServerPacketId.UserId, (ServerPacketId)header.Id);
        Assert.Equal(4u, header.Length);
        Assert.Equal(1337, reader.ReadInt32());
    }

    [Fact]
    public void Int32List_RoundTrips()
    {
        int[] ids = [1, 2, 3, 99];
        var writer = new BanchoPacketWriter(32);
        writer.WriteInt32List(ids);

        var reader = new BanchoPacketReader(writer.ToArray());
        Assert.Equal(ids, reader.ReadInt32List());
    }

    [Fact]
    public void Reader_ThrowsOnTruncatedPayload()
    {
        // A header claiming 4 payload bytes, but none follow.
        var writer = new BanchoPacketWriter(8);
        writer.WriteUInt16((ushort)ServerPacketId.UserId);
        writer.WriteByte(0);
        writer.WriteUInt32(4);

        var bytes = writer.ToArray();
        Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new BanchoPacketReader(bytes);
            _ = reader.ReadHeader();
            _ = reader.ReadInt32();
        });
    }
}
