using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace TomYum.Server.Bancho;

/// <summary>
/// The PROXY protocol header (HAProxy's, versions 1 and 2) a TCP relay such as frp puts in front of
/// a forwarded connection, so the server knows the player's real address (docs/old-clients.md: TCP
/// bancho reaches the server through a relay). Only read when the listener is told to expect it.
/// </summary>
public static class ProxyProtocol
{
    private static readonly byte[] V2Signature = [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];

    /// <summary>What reading a header from the start of a connection found.</summary>
    public enum Result
    {
        /// <summary>Not enough bytes yet.</summary>
        NeedMore,

        /// <summary>A header: <c>consumed</c> bytes long, with the client's address (null for a LOCAL or unknown one).</summary>
        Ok,

        /// <summary>Not a PROXY header.</summary>
        Invalid,
    }

    /// <summary>Reads a PROXY header from <paramref name="data"/>, the first bytes of a connection.</summary>
    public static Result TryRead(ReadOnlySpan<byte> data, out int consumed, out IPAddress? address)
    {
        consumed = 0;
        address = null;
        if (data.IsEmpty)
            return Result.NeedMore;

        // Version 2: binary.
        if (data[0] == V2Signature[0])
        {
            if (data.Length < 16)
                return V2Signature.AsSpan(0, Math.Min(data.Length, 12)).SequenceEqual(data[..Math.Min(data.Length, 12)]) ? Result.NeedMore : Result.Invalid;
            if (!data[..12].SequenceEqual(V2Signature) || data[12] >> 4 != 2)
                return Result.Invalid;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[14..]);
            if (data.Length < 16 + length)
                return Result.NeedMore;
            consumed = 16 + length;
            var command = data[12] & 0x0F; // 0 LOCAL (the relay's own check), 1 PROXY
            var family = data[13] >> 4;    // 1 IPv4, 2 IPv6
            var addresses = data.Slice(16, length);
            if (command == 1 && family == 1 && length >= 12)
                address = new IPAddress(addresses[..4]);
            else if (command == 1 && family == 2 && length >= 36)
                address = new IPAddress(addresses[..16]);
            return Result.Ok;
        }

        // Version 1: "PROXY TCP4 <client> <server> <client port> <server port>\r\n", at most 107 bytes.
        if (data[0] != (byte)'P')
            return Result.Invalid;
        var end = data.IndexOf((byte)'\n');
        if (end < 0)
            return data.Length < 107 && "PROXY "u8[..Math.Min(data.Length, 6)].SequenceEqual(data[..Math.Min(data.Length, 6)])
                ? Result.NeedMore
                : Result.Invalid;
        var line = Encoding.ASCII.GetString(data[..end]).TrimEnd('\r');
        var parts = line.Split(' ');
        if (parts.Length < 2 || parts[0] != "PROXY")
            return Result.Invalid;
        consumed = end + 1;
        if (parts[1] is "TCP4" or "TCP6" && parts.Length >= 3 && IPAddress.TryParse(parts[2], out var parsed))
            address = parsed;
        return Result.Ok;
    }
}
