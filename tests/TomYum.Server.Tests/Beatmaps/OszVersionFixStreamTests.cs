using System.Buffers.Binary;
using System.IO.Compression;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>Sets repacked in 2014 say zip 6.3 in their local headers; old osu! reads only up to 4.5.</summary>
public class OszVersionFixStreamTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public void PlainEntries_Say20_AndNothingElseChanges(int chunk)
    {
        var archive = Archive(CompressionLevel.NoCompression, CompressionLevel.Optimal);
        var broken = WithVersions(archive, 63, null);
        var expected = WithVersions(archive, 20, 20);

        var output = ReadAll(new OszVersionFixStream(new MemoryStream(broken)), chunk);

        Assert.Equal(expected, output);
        Assert.Equal(broken.Length, output.Length);
        using var zip = new ZipArchive(new MemoryStream(output));
        Assert.Equal(["20.mp3", "map.osu", "bg.jpg"], zip.Entries.Select(e => e.FullName));
    }

    [Fact]
    public void AMethodOldOsuCantRead_KeepsItsVersion()
    {
        var archive = WithVersions(Archive(CompressionLevel.NoCompression, CompressionLevel.NoCompression), 63, null);
        // The second entry says LZMA (14), in its local header and the central directory.
        var lzma = (byte[])archive.Clone();
        var local = Headers(lzma, 0x04034b50).ToList();
        var central = Headers(lzma, 0x02014b50).ToList();
        BinaryPrimitives.WriteUInt16LittleEndian(lzma.AsSpan(local[1] + 8), 14);
        BinaryPrimitives.WriteUInt16LittleEndian(lzma.AsSpan(central[1] + 10), 14);

        var output = ReadAll(new OszVersionFixStream(new MemoryStream(lzma)), 4096);

        Assert.Equal(20, BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(local[0] + 4)));
        Assert.Equal(63, BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(local[1] + 4)));
        Assert.Equal(20, BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(local[2] + 4)));
    }

    [Fact]
    public void SomethingThatIsntAZip_PassesAsItIs()
    {
        var bytes = Enumerable.Range(0, 10_000).Select(i => (byte)(i * 31)).ToArray();
        Assert.Equal(bytes, ReadAll(new OszVersionFixStream(new MemoryStream(bytes)), 333));
    }

    private static byte[] Archive(CompressionLevel audio, CompressionLevel map)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "20.mp3", audio, new byte[50_000]);
            Add(zip, "map.osu", map, "osu file format v3\n\n[General]\nAudioFilename: 20.mp3\n"u8.ToArray());
            Add(zip, "bg.jpg", CompressionLevel.NoCompression, new byte[3_000]);
        }
        return memory.ToArray();
    }

    private static void Add(ZipArchive zip, string name, CompressionLevel level, byte[] content)
    {
        using var stream = zip.CreateEntry(name, level).Open();
        stream.Write(content);
    }

    /// <summary>The archive with every local header's version set to <paramref name="local"/> (and the central directory's, when given).</summary>
    private static byte[] WithVersions(byte[] archive, ushort local, ushort? central)
    {
        var copy = (byte[])archive.Clone();
        foreach (var at in Headers(copy, 0x04034b50))
            BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(at + 4), local);
        if (central is { } version)
        {
            foreach (var at in Headers(copy, 0x02014b50))
                BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(at + 6), version);
        }
        return copy;
    }

    /// <summary>Where the archive's local headers (or central directory entries) start, from its central directory.</summary>
    private static IEnumerable<int> Headers(byte[] archive, uint signature)
    {
        var end = archive.Length - 22;
        while (BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(end)) != 0x06054b50)
            end--;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(end + 10));
        var at = (int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(end + 16));
        for (var i = 0; i < count; i++)
        {
            yield return signature == 0x02014b50 ? at : (int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(at + 42));
            at += 46 + BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(at + 28))
                     + BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(at + 30))
                     + BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(at + 32));
        }
    }

    private static byte[] ReadAll(Stream stream, int chunk)
    {
        using var output = new MemoryStream();
        var buffer = new byte[chunk];
        int read;
        while ((read = stream.Read(buffer, 0, chunk)) > 0)
            output.Write(buffer, 0, read);
        return output.ToArray();
    }
}
