using System.Buffers.Binary;
using System.Text;

namespace TomYum.Bancho;

/// <summary>
/// Builds an osu!stable bancho packet stream. Writes the little-endian primitives and
/// osu!-specific encodings (presence-byte + ULEB128 strings) and frames whole packets
/// with the 7-byte header (u16 id + pad byte + u32 length) defined in
/// <c>memories/protocols/bancho-stable.md</c> §2.
/// </summary>
/// <remarks>
/// Hand-rolled with no external dependencies. Backed by a growable byte buffer; call
/// <see cref="ToArray"/> to get the framed bytes to put on an HTTP response body.
/// Not thread-safe — build per response, then flush.
/// </remarks>
public sealed class BanchoPacketWriter
{
    private byte[] _buffer;
    private int _length;

    /// <summary>Creates a writer with an optional initial capacity hint.</summary>
    public BanchoPacketWriter(int initialCapacity = 256)
    {
        _buffer = new byte[Math.Max(initialCapacity, PacketHeader.Size)];
        _length = 0;
    }

    /// <summary>Number of bytes written so far.</summary>
    public int Length => _length;

    /// <summary>
    /// Writes a complete packet: the <paramref name="id"/> header plus the payload produced
    /// by <paramref name="writePayload"/>, back-patching the payload length once known.
    /// This is the primary building block for server→client packets.
    /// </summary>
    /// <param name="id">The server packet id.</param>
    /// <param name="writePayload">Callback that writes the payload via this writer; may be <c>null</c> for an empty payload.</param>
    public BanchoPacketWriter WritePacket(ServerPacketId id, Action<BanchoPacketWriter>? writePayload)
    {
        WriteUInt16((ushort)id);
        WriteByte(0); // compression/pad byte, always 0.

        // Reserve 4 bytes for the payload length, remember where, write payload, back-patch.
        var lengthOffset = _length;
        WriteUInt32(0);
        var payloadStart = _length;

        writePayload?.Invoke(this);

        var payloadLength = _length - payloadStart;
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(lengthOffset, sizeof(uint)), (uint)payloadLength);
        return this;
    }

    /// <summary>Writes a packet whose entire payload is a single i32 (USER_ID, PROTOCOL_VERSION, PRIVILEGES, …).</summary>
    public BanchoPacketWriter WriteInt32Packet(ServerPacketId id, int value) =>
        WritePacket(id, w => w.WriteInt32(value));

    /// <summary>Writes a packet whose entire payload is a single osu! string (NOTIFICATION, MAIN_MENU_ICON, …).</summary>
    public BanchoPacketWriter WriteStringPacket(ServerPacketId id, string value) =>
        WritePacket(id, w => w.WriteString(value));

    /// <summary>Writes a single byte.</summary>
    public BanchoPacketWriter WriteByte(byte value)
    {
        EnsureCapacity(sizeof(byte));
        _buffer[_length++] = value;
        return this;
    }

    /// <summary>Writes a signed byte.</summary>
    public BanchoPacketWriter WriteSByte(sbyte value) => WriteByte((byte)value);

    /// <summary>Writes an unsigned 16-bit little-endian integer.</summary>
    public BanchoPacketWriter WriteUInt16(ushort value)
    {
        EnsureCapacity(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length, sizeof(ushort)), value);
        _length += sizeof(ushort);
        return this;
    }

    /// <summary>Writes a signed 16-bit little-endian integer.</summary>
    public BanchoPacketWriter WriteInt16(short value) => WriteUInt16((ushort)value);

    /// <summary>Writes an unsigned 32-bit little-endian integer.</summary>
    public BanchoPacketWriter WriteUInt32(uint value)
    {
        EnsureCapacity(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length, sizeof(uint)), value);
        _length += sizeof(uint);
        return this;
    }

    /// <summary>Writes a signed 32-bit little-endian integer.</summary>
    public BanchoPacketWriter WriteInt32(int value) => WriteUInt32((uint)value);

    /// <summary>Writes an unsigned 64-bit little-endian integer.</summary>
    public BanchoPacketWriter WriteUInt64(ulong value)
    {
        EnsureCapacity(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_length, sizeof(ulong)), value);
        _length += sizeof(ulong);
        return this;
    }

    /// <summary>Writes a signed 64-bit little-endian integer.</summary>
    public BanchoPacketWriter WriteInt64(long value) => WriteUInt64((ulong)value);

    /// <summary>Writes a 32-bit little-endian float.</summary>
    public BanchoPacketWriter WriteSingle(float value)
    {
        EnsureCapacity(sizeof(float));
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_length, sizeof(float)), value);
        _length += sizeof(float);
        return this;
    }

    /// <summary>Writes a 64-bit little-endian double.</summary>
    public BanchoPacketWriter WriteDouble(double value)
    {
        EnsureCapacity(sizeof(double));
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_length, sizeof(double)), value);
        _length += sizeof(double);
        return this;
    }

    /// <summary>Writes a 7-bit-encoded ULEB128 unsigned integer.</summary>
    public BanchoPacketWriter WriteUleb128(uint value)
    {
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
            }

            WriteByte(b);
        }
        while (value != 0);

        return this;
    }

    /// <summary>
    /// Writes an osu! string: <c>0x00</c> for null/empty, otherwise <c>0x0b</c> + ULEB128
    /// UTF-8 byte length + the UTF-8 bytes.
    /// </summary>
    public BanchoPacketWriter WriteString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return WriteByte(0x00);
        }

        WriteByte(0x0b);
        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteUleb128((uint)byteCount);
        EnsureCapacity(byteCount);
        Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_length, byteCount));
        _length += byteCount;
        return this;
    }

    /// <summary>Writes an osu! i32 list: a u16 count followed by each i32 value (osu type 18).</summary>
    public BanchoPacketWriter WriteInt32List(IReadOnlyList<int> values)
    {
        WriteUInt16((ushort)values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            WriteInt32(values[i]);
        }

        return this;
    }

    /// <summary>Appends raw bytes verbatim (e.g. re-broadcasting a spectate frame bundle).</summary>
    public BanchoPacketWriter WriteRaw(ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length, bytes.Length));
        _length += bytes.Length;
        return this;
    }

    /// <summary>Returns the framed packet bytes written so far as a new array.</summary>
    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    private void EnsureCapacity(int additional)
    {
        var required = _length + additional;
        if (required <= _buffer.Length)
        {
            return;
        }

        var newCapacity = _buffer.Length * 2;
        if (newCapacity < required)
        {
            newCapacity = required;
        }

        Array.Resize(ref _buffer, newCapacity);
    }
}
