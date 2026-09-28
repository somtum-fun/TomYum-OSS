using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

// CA5351: MD5 is how the .osz2 format derives its key and checks its contents, not our choice.
#pragma warning disable CA5351

namespace TomYum.Server.Beatmaps.Osz2;

/// <summary>Metadata fields of an <c>.osz2</c> header (the ones the server reads).</summary>
public static class Osz2Meta
{
    public const short Title = 0;
    public const short Artist = 1;
    public const short Creator = 2;
    public const short Version = 3;
    public const short Source = 4;
    public const short Tags = 5;
    public const short BeatmapSetId = 9;
    public const short TitleUnicode = 12;
    public const short ArtistUnicode = 13;
}

/// <summary>A file inside an <c>.osz2</c> package, decrypted.</summary>
public sealed record Osz2File(string Name, byte[] Content, DateTime CreatedUtc, DateTime ModifiedUtc);

/// <summary>
/// Reads the encrypted <c>.osz2</c> packages the osu!stable editor uploads when submitting a
/// beatmap set: the plain header (metadata, which beatmap id each <c>.osu</c> has) and every file,
/// decrypted and checked against the package's hashes.
/// </summary>
/// <remarks>
/// Layout and ciphers as documented by Lekuruu's osz2-go (MIT, see THIRD-PARTY-NOTICES.md):
/// magic <c>EC 48 4F</c>, version, IV, three hashes, metadata, beatmap ids, an XTEA-encrypted
/// known block that proves the key, the XXTEA-encrypted file table, then each file's
/// XXTEA-encrypted length and content. The key is the MD5 of <c>creator + "yhxyfjo5" + setId</c>.
/// </remarks>
public sealed class Osz2Package
{
    private static readonly byte[] Magic = [0xEC, 0x48, 0x4F];

    // The first 64 bytes of FastRandom(1990): decrypting them proves the key is right.
    private static readonly byte[] KnownPlain =
    [
        0x55, 0xAA, 0x74, 0x10, 0x2B, 0x56, 0xB3, 0x9E, 0x25, 0x9E, 0xFE, 0xB7, 0xBE, 0x06, 0xFC, 0xF2,
        0xB6, 0x3C, 0x6F, 0x47, 0x7E, 0x38, 0x69, 0x43, 0x80, 0x89, 0x25, 0x00, 0xCC, 0xB6, 0xFE, 0x12,
        0xA9, 0xB2, 0x4A, 0x2C, 0x96, 0xD5, 0xEA, 0x26, 0x42, 0x31, 0xAF, 0x0A, 0x0D, 0xAE, 0x00, 0xED,
        0xFE, 0x96, 0xA6, 0x94, 0x99, 0xA7, 0x90, 0xE4, 0x68, 0xBF, 0xC6, 0x97, 0x5B, 0x1B, 0x5E, 0x7F,
    ];

    private const int MaxFiles = 5000;

    private Osz2Package(Dictionary<short, string> metadata, Dictionary<string, int> beatmapIds, List<Osz2File> files)
    {
        Metadata = metadata;
        BeatmapIds = beatmapIds;
        Files = files;
    }

    /// <summary>Header metadata by <see cref="Osz2Meta"/> field.</summary>
    public IReadOnlyDictionary<short, string> Metadata { get; }

    /// <summary>The beatmap id of each <c>.osu</c> file (-1 when it has none yet).</summary>
    public IReadOnlyDictionary<string, int> BeatmapIds { get; }

    /// <summary>Every file, in package order.</summary>
    public IReadOnlyList<Osz2File> Files { get; }

    /// <summary>A header field, or null.</summary>
    public string? Get(short field) => Metadata.TryGetValue(field, out var value) ? value : null;

    /// <summary>Reads a package; throws <see cref="InvalidDataException"/> when it isn't a valid one.</summary>
    public static Osz2Package Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            return ReadCore(data);
        }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentOutOfRangeException or ArgumentException or OverflowException or DecoderFallbackException)
        {
            throw new InvalidDataException("The .osz2 package is damaged.", ex);
        }
    }

    /// <summary>The files as a plain <c>.osz</c> (zip).</summary>
    public byte[] ToOsz()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Files)
            {
                var entry = zip.CreateEntry(file.Name, IsCompressed(file.Name) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                if (file.ModifiedUtc.Year is >= 1980 and <= 2107)
                    entry.LastWriteTime = file.ModifiedUtc;
                using var stream = entry.Open();
                stream.Write(file.Content);
            }
        }
        return output.ToArray();
    }

    private static bool IsCompressed(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".mp3" or ".ogg" or ".jpg" or ".jpeg" or ".png" or ".avi" or ".flv" or ".mp4" or ".mkv" or ".webm";

    private static Osz2Package ReadCore(byte[] data)
    {
        var reader = new BinaryReader(new MemoryStream(data, writable: false), Encoding.UTF8);
        if (!reader.ReadBytes(3).AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("That isn't an .osz2 package.");
        reader.ReadByte(); // version
        reader.ReadBytes(16); // IV
        var metadataHash = reader.ReadBytes(16);
        var fileInfoHash = reader.ReadBytes(16);
        reader.ReadBytes(16); // body hash

        // Metadata, checked against its hash (computed over the fields as written).
        var metadataStart = reader.BaseStream.Position;
        var count = reader.ReadInt32();
        if (count < 0 || count > (data.Length - metadataStart) / 3)
            throw new InvalidDataException("The .osz2 metadata is damaged.");
        var metadata = new Dictionary<short, string>();
        for (var i = 0; i < count; i++)
        {
            var field = reader.ReadInt16();
            if (!metadata.TryAdd(field, reader.ReadString()))
                throw new InvalidDataException("The .osz2 metadata is damaged.");
        }
        var metadataBytes = data.AsSpan((int)metadataStart, (int)(reader.BaseStream.Position - metadataStart));
        if (!Hash(metadataBytes, count * 3, 0xA7).AsSpan().SequenceEqual(metadataHash))
            throw new InvalidDataException("The .osz2 metadata doesn't match its hash.");

        var mapCount = reader.ReadInt32();
        if (mapCount < 0 || mapCount > (data.Length - reader.BaseStream.Position) / 5)
            throw new InvalidDataException("The .osz2 beatmap list is damaged.");
        var beatmapIds = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < mapCount; i++)
        {
            var name = CanonicalPath(reader.ReadString());
            if (!beatmapIds.TryAdd(name, reader.ReadInt32()))
                throw new InvalidDataException("The .osz2 beatmap list is damaged.");
        }

        if (metadata.GetValueOrDefault(Osz2Meta.Creator) is not { } creator || metadata.GetValueOrDefault(Osz2Meta.BeatmapSetId) is not { } setId)
            throw new InvalidDataException("The .osz2 package has no creator or set id.");
        // The client derives it with .NET's ASCII encoding (non-ASCII characters become '?').
        var crypto = new Osz2Crypto(MD5.HashData(Encoding.ASCII.GetBytes(creator + "yhxyfjo5" + setId)));

        var known = reader.ReadBytes(KnownPlain.Length);
        crypto.DecryptXtea(known);
        if (!known.AsSpan().SequenceEqual(KnownPlain))
            throw new InvalidDataException("The .osz2 package can't be decrypted.");

        var infoLength = reader.ReadInt32() - LengthAdjustment(fileInfoHash);
        var infoStart = reader.BaseStream.Position;
        if (infoLength < 4 || infoLength > data.Length - infoStart)
            throw new InvalidDataException("The .osz2 file table is damaged.");
        var dataStart = infoStart + infoLength;
        var files = ReadFiles(data, crypto, data.AsSpan((int)infoStart, (int)infoLength).ToArray(), fileInfoHash, dataStart);

        foreach (var name in beatmapIds.Keys)
        {
            if (!files.Any(f => f.Name == name) || !IsBeatmapFile(name))
                throw new InvalidDataException("The .osz2 beatmap list names a file it doesn't have.");
        }
        return new Osz2Package(metadata, beatmapIds, files);
    }

    private static List<Osz2File> ReadFiles(byte[] data, Osz2Crypto crypto, byte[] table, byte[] fileInfoHash, long dataStart)
    {
        var frames = new FrameReader(table, crypto);
        var count = frames.ReadInt32();
        if (count <= 0 || count > MaxFiles || count > table.Length / 33)
            throw new InvalidDataException("The .osz2 file table is damaged.");
        if (!Hash(table, count * 4, 0xD1).AsSpan().SequenceEqual(fileInfoHash))
            throw new InvalidDataException("The .osz2 file table doesn't match its hash.");

        var offset = frames.ReadInt32();
        if (offset != 0)
            throw new InvalidDataException("The .osz2 file table is damaged.");

        var dataSize = data.Length - dataStart;
        var files = new List<Osz2File>(count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var name = CanonicalPath(frames.ReadString());
            if (!names.Add(name))
                throw new InvalidDataException("The .osz2 package has a file twice.");
            var hash = frames.Read(16);
            var created = frames.ReadInt64();
            var modified = frames.ReadInt64();
            var next = i + 1 < count ? frames.ReadInt32() : dataSize;
            if (next < offset || next > dataSize || next - offset < 4)
                throw new InvalidDataException("The .osz2 file table is damaged.");

            // Each file: its length (one frame), then its content (one frame).
            var start = (int)(dataStart + offset);
            var size = (int)(next - offset - 4);
            var length = data.AsSpan(start, 4).ToArray();
            crypto.DecryptXxtea(length);
            if (BinaryPrimitives.ReadInt32LittleEndian(length) != size)
                throw new InvalidDataException("The .osz2 file table is damaged.");

            var content = data.AsSpan(start + 4, size).ToArray();
            crypto.DecryptXxtea(content);
            if (!MD5.HashData(content).AsSpan().SequenceEqual(hash) && !LegacyHashMatches(data, start, size, crypto, hash))
                throw new InvalidDataException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name} in the .osz2 package is damaged."));

            files.Add(new Osz2File(name, content, FromDotNet(created), FromDotNet(modified)));
            offset = (int)next;
        }
        return files;
    }

    /// <summary>Packages from older clients hashed the content decrypted together with its length prefix.</summary>
    private static bool LegacyHashMatches(byte[] data, int start, int size, Osz2Crypto crypto, byte[] hash)
    {
        var framed = data.AsSpan(start, size + 4).ToArray();
        crypto.DecryptXxtea(framed);
        return MD5.HashData(framed.AsSpan(4)).AsSpan().SequenceEqual(hash);
    }

    /// <summary>The format's MD5 variant: one byte flipped with a mask, halves swapped, byte 5 masked.</summary>
    private static byte[] Hash(ReadOnlySpan<byte> data, int position, byte mask)
    {
        var input = data.ToArray();
        if (position < input.Length)
            input[position] ^= mask;
        var hash = MD5.HashData(input);
        for (var i = 0; i < 8; i++)
            (hash[i], hash[i + 8]) = (hash[i + 8], hash[i]);
        hash[5] ^= 0x2D;
        return hash;
    }

    private static int LengthAdjustment(byte[] fileInfoHash)
    {
        long adjustment = 0;
        for (var i = 0; i < 16; i += 2)
            adjustment += fileInfoHash[i] | ((long)fileInfoHash[i + 1] << 17);
        return (int)adjustment;
    }

    private static DateTime FromDotNet(long value)
    {
        var ticks = value & 0x3FFFFFFFFFFFFFFF;
        return ticks is >= 0 and <= 3155378975999999999 ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.UnixEpoch;
    }

    private static bool IsBeatmapFile(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".osu" or ".osc";

    /// <summary>Forward slashes, no <c>..</c> or rooted paths.</summary>
    private static string CanonicalPath(string name)
    {
        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".." || p.Contains(':', StringComparison.Ordinal)) || name.StartsWith('/'))
            throw new InvalidDataException("The .osz2 package has an invalid file name.");
        return string.Join('/', parts);
    }

    /// <summary>The file table: every field was encrypted as its own write, so it's decrypted the same way.</summary>
    private sealed class FrameReader(byte[] buffer, Osz2Crypto crypto)
    {
        private int _position;

        public byte[] Read(int count)
        {
            if (count < 0 || count > buffer.Length - _position)
                throw new EndOfStreamException();
            var frame = buffer.AsSpan(_position, count).ToArray();
            _position += count;
            crypto.DecryptXxtea(frame);
            return frame;
        }

        public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Read(4));

        public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Read(8));

        public string ReadString()
        {
            // .NET's 7-bit length prefix, one byte (one frame) at a time.
            var length = 0;
            for (var shift = 0; ; shift += 7)
            {
                if (shift > 28)
                    throw new InvalidDataException("The .osz2 file table is damaged.");
                var b = Read(1)[0];
                length |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    break;
            }
            return length == 0 ? string.Empty : Encoding.UTF8.GetString(Read(length));
        }
    }
}
