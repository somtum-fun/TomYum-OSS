using System.Buffers.Binary;
using System.Text;

namespace TomYum.Bancho;

/// <summary>
/// Forward-only reader over an osu!stable bancho packet stream. Reads the little-endian
/// primitives and osu!-specific encodings (presence-byte + ULEB128 strings) defined in
/// <c>memories/protocols/bancho-stable.md</c> §2.
/// </summary>
/// <remarks>
/// Hand-rolled with no external dependencies (ADR: TomYum.Bancho is a pure library).
/// All multi-byte integers are little-endian. The reader is a mutable <see langword="ref struct"/>
/// so it never escapes to the heap and stays allocation-free on the hot path.
/// </remarks>
public ref struct BanchoPacketReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    /// <summary>Creates a reader over <paramref name="buffer"/>, starting at offset 0.</summary>
    public BanchoPacketReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>The current read offset into the buffer.</summary>
    public readonly int Position => _position;

    /// <summary>Number of bytes left to read.</summary>
    public readonly int Remaining => _buffer.Length - _position;

    /// <summary>Whether at least one more packet header (7 bytes) could be present.</summary>
    public readonly bool CanReadHeader => Remaining >= PacketHeader.Size;

    /// <summary>
    /// Reads a packet header (u16 id + pad byte + u32 payload length). The pad/compression
    /// byte at offset 2 is read and discarded — it is effectively always 0.
    /// </summary>
    public PacketHeader ReadHeader()
    {
        var id = (ClientPacketId)ReadUInt16();
        _ = ReadByte(); // compression/pad byte; always 0 in practice.
        var length = ReadUInt32();
        return new PacketHeader(id, length);
    }

    /// <summary>Reads a single byte.</summary>
    public byte ReadByte()
    {
        EnsureAvailable(sizeof(byte));
        return _buffer[_position++];
    }

    /// <summary>Reads a signed byte.</summary>
    public sbyte ReadSByte() => (sbyte)ReadByte();

    /// <summary>Reads an unsigned 16-bit little-endian integer.</summary>
    public ushort ReadUInt16()
    {
        EnsureAvailable(sizeof(ushort));
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_position, sizeof(ushort)));
        _position += sizeof(ushort);
        return value;
    }

    /// <summary>Reads a signed 16-bit little-endian integer.</summary>
    public short ReadInt16() => (short)ReadUInt16();

    /// <summary>Reads an unsigned 32-bit little-endian integer.</summary>
    public uint ReadUInt32()
    {
        EnsureAvailable(sizeof(uint));
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_position, sizeof(uint)));
        _position += sizeof(uint);
        return value;
    }

    /// <summary>Reads a signed 32-bit little-endian integer.</summary>
    public int ReadInt32() => (int)ReadUInt32();

    /// <summary>Reads an unsigned 64-bit little-endian integer.</summary>
    public ulong ReadUInt64()
    {
        EnsureAvailable(sizeof(ulong));
        var value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(_position, sizeof(ulong)));
        _position += sizeof(ulong);
        return value;
    }

    /// <summary>Reads a signed 64-bit little-endian integer.</summary>
    public long ReadInt64() => (long)ReadUInt64();

    /// <summary>Reads a 32-bit little-endian float.</summary>
    public float ReadSingle()
    {
        EnsureAvailable(sizeof(float));
        var value = BinaryPrimitives.ReadSingleLittleEndian(_buffer.Slice(_position, sizeof(float)));
        _position += sizeof(float);
        return value;
    }

    /// <summary>Reads a 64-bit little-endian double.</summary>
    public double ReadDouble()
    {
        EnsureAvailable(sizeof(double));
        var value = BinaryPrimitives.ReadDoubleLittleEndian(_buffer.Slice(_position, sizeof(double)));
        _position += sizeof(double);
        return value;
    }

    /// <summary>
    /// Reads a 7-bit-encoded ULEB128 unsigned integer (osu! string length prefix).
    /// </summary>
    public uint ReadUleb128()
    {
        uint result = 0;
        var shift = 0;
        while (true)
        {
            var b = ReadByte();
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
            if (shift >= 35)
            {
                throw new InvalidDataException("ULEB128 value is too large for a 32-bit integer.");
            }
        }
    }

    /// <summary>
    /// Reads an osu! string: a <c>0x00</c> presence byte means empty; <c>0x0b</c> means a
    /// ULEB128 length followed by that many UTF-8 bytes. Any other presence byte is invalid.
    /// </summary>
    public string ReadString()
    {
        var marker = ReadByte();
        switch (marker)
        {
            case 0x00:
                return string.Empty;
            case 0x0b:
                var length = checked((int)ReadUleb128());
                if (length == 0)
                {
                    return string.Empty;
                }

                EnsureAvailable(length);
                var text = Encoding.UTF8.GetString(_buffer.Slice(_position, length));
                _position += length;
                return text;
            default:
                throw new InvalidDataException($"Invalid string presence byte 0x{marker:x2} (expected 0x00 or 0x0b).");
        }
    }

    /// <summary>
    /// Reads an osu! i32 list: a u16 count followed by that many i32 values
    /// (osu type 18). Used for friends lists, presence-bundle ids, etc.
    /// </summary>
    public int[] ReadInt32List()
    {
        var count = ReadUInt16();
        if (count == 0)
        {
            return [];
        }

        var values = new int[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = ReadInt32();
        }

        return values;
    }

    /// <summary>Returns the next <paramref name="length"/> bytes of payload and advances past them.</summary>
    public ReadOnlySpan<byte> ReadBytes(int length)
    {
        EnsureAvailable(length);
        var slice = _buffer.Slice(_position, length);
        _position += length;
        return slice;
    }

    /// <summary>Skips <paramref name="length"/> bytes (e.g. an unhandled packet's payload).</summary>
    public void Skip(int length)
    {
        EnsureAvailable(length);
        _position += length;
    }

    private readonly void EnsureAvailable(int count)
    {
        if (Remaining < count)
        {
            throw new InvalidDataException(
                $"Unexpected end of bancho packet stream: needed {count} byte(s) at offset {_position}, {Remaining} remaining.");
        }
    }
}

/// <summary>
/// A decoded bancho packet header: the <see cref="ClientPacketId"/> and the byte length of
/// the payload that follows. The header itself is <see cref="Size"/> bytes on the wire.
/// </summary>
/// <param name="Id">The client packet id (cast to <see cref="ServerPacketId"/> when reading server output).</param>
/// <param name="Length">Payload length in bytes.</param>
public readonly record struct PacketHeader(ClientPacketId Id, uint Length)
{
    /// <summary>Wire size of a packet header in bytes: u16 id + 1 pad byte + u32 length.</summary>
    public const int Size = 7;
}
