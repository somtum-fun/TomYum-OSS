using System.Buffers.Binary;

namespace TomYum.Server.Beatmaps.Osz2;

/// <summary>
/// The ciphers of osu!'s <c>.osz2</c> packages: XTEA (the key check), XXTEA (file table and file
/// contents) and the byte cipher both use for tails too short for a block. Decryption only: the
/// server reads packages, it never writes them.
/// </summary>
/// <remarks>
/// Ported from Lekuruu's osz2-go (MIT, Copyright (c) 2026 Levi; see THIRD-PARTY-NOTICES.md).
/// Every buffer written as one piece is encrypted as one frame, so callers decrypt exactly the
/// pieces that were written.
/// </remarks>
internal sealed class Osz2Crypto
{
    private const uint Delta = 0x9e3779b9;
    private const int TeaRounds = 32;
    private const int MaxWords = 16;

    /// <summary>XXTEA works on blocks of this many bytes.</summary>
    public const int BlockBytes = MaxWords * 4;

    private readonly uint[] _key;
    private readonly byte[] _keyBytes;

    public Osz2Crypto(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 16)
            throw new ArgumentException("osz2 keys are 16 bytes.", nameof(key));
        _keyBytes = (byte[])key.Clone();
        _key = new uint[4];
        for (var i = 0; i < 4; i++)
            _key[i] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(i * 4));
    }

    /// <summary>XTEA over 8-byte words; the tail goes through the byte cipher.</summary>
    public void DecryptXtea(Span<byte> buffer)
    {
        var words = buffer.Length / 8;
        for (var i = 0; i < words; i++)
        {
            var block = buffer.Slice(i * 8, 8);
            var v0 = BinaryPrimitives.ReadUInt32LittleEndian(block);
            var v1 = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
            var sum = unchecked(Delta * TeaRounds);
            for (var round = 0; round < TeaRounds; round++)
            {
                v1 -= (((v0 << 4) ^ (v0 >> 5)) + v0) ^ (sum + _key[(sum >> 11) & 3]);
                sum -= Delta;
                v0 -= (((v1 << 4) ^ (v1 >> 5)) + v1) ^ (sum + _key[sum & 3]);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(block, v0);
            BinaryPrimitives.WriteUInt32LittleEndian(block[4..], v1);
        }

        var rest = buffer.Length % 8;
        if (rest > 0)
            DecryptBytes(buffer[(words * 8)..]);
    }

    /// <summary>XXTEA over 64-byte blocks; a shorter tail as one smaller block, its last bytes through the byte cipher.</summary>
    public void DecryptXxtea(Span<byte> buffer)
    {
        var blocks = buffer.Length / BlockBytes;
        for (var i = 0; i < blocks; i++)
            DecryptWords(buffer.Slice(i * BlockBytes, BlockBytes), MaxWords);

        var rest = buffer[(blocks * BlockBytes)..];
        if (rest.Length == 0)
            return;

        var words = rest.Length / 4;
        if (words > 1)
        {
            DecryptWords(rest[..(words * 4)], words);
            rest = rest[(words * 4)..];
            if (rest.Length == 0)
                return;
        }
        DecryptBytes(rest);
    }

    private void DecryptWords(Span<byte> data, int n)
    {
        Span<uint> v = stackalloc uint[MaxWords];
        v = v[..n];
        for (var i = 0; i < n; i++)
            v[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);

        var rounds = (uint)(6 + 52 / n);
        var sum = unchecked(rounds * Delta);
        var y = v[0];
        uint z;
        do
        {
            var e = (sum >> 2) & 3;
            int p;
            for (p = n - 1; p > 0; p--)
            {
                z = v[p - 1];
                v[p] -= (((z >> 5) ^ (y << 2)) + ((y >> 3) ^ (z << 4))) ^ ((sum ^ y) + (_key[(p & 3) ^ (int)e] ^ z));
                y = v[p];
            }
            z = v[n - 1];
            v[0] -= (((z >> 5) ^ (y << 2)) + ((y >> 3) ^ (z << 4))) ^ ((sum ^ y) + (_key[(p & 3) ^ (int)e] ^ z));
            y = v[0];
            sum -= Delta;
        }
        while (sum != 0);

        for (var i = 0; i < n; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(data[(i * 4)..], v[i]);
    }

    private void DecryptBytes(Span<byte> buffer)
    {
        byte previous = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            var encrypted = buffer[i];
            var value = RotateLeft(buffer[i], (int)(~(uint)previous % 7));
            value ^= RotateLeft(_keyBytes[15 - i % 16], (previous + buffer.Length - i) % 7);
            buffer[i] = unchecked((byte)(value - (_keyBytes[i % 16] >> 2)));
            previous = encrypted;
        }
    }

    private static byte RotateLeft(byte value, int bits) => (byte)((value << bits) | (value >> (8 - bits)));
}
