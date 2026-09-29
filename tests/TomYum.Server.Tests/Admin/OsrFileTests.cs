using System.Text;
using TomYum.Domain.Entities;
using TomYum.Server.Admin;
using Xunit;

namespace TomYum.Server.Tests.Admin;

/// <summary>Relax/Autopilot replays downloaded from the website: a whole .osr from the score and its frames.</summary>
public sealed class OsrFileTests
{
    [Fact]
    public void TheFile_IsLaidOutLikeOsus()
    {
        var score = new Score
        {
            Id = 1234, Mode = 4, BeatmapMd5 = "285b4d5d3de627e642431948c5c1fd3d", Count300 = 400, Count100 = 20, Count50 = 3, CountGeki = 50,
            CountKatu = 10, CountMiss = 2, ScoreValue = 5_000_000, MaxCombo = 900, Perfect = false, Mods = 128 | 8, Grade = "A",
            SubmittedAt = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero),
        };
        byte[] frames = [1, 2, 3, 4, 5];

        using var reader = new BinaryReader(new MemoryStream(OsrFile.Build(score, "Lilian", frames)));

        Assert.Equal(0, reader.ReadByte());   // osu! (Relax is a mod, not a mode, in the file)
        Assert.Equal(20250101, reader.ReadInt32());
        Assert.Equal(score.BeatmapMd5, ReadString(reader));
        Assert.Equal("Lilian", ReadString(reader));
        Assert.Equal(32, ReadString(reader).Length);
        Assert.Equal((400, 20, 3, 50, 10, 2), (reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16()));
        Assert.Equal(5_000_000, reader.ReadInt32());
        Assert.Equal(900, reader.ReadUInt16());
        Assert.False(reader.ReadBoolean());
        Assert.Equal(128 | 8, reader.ReadInt32());
        Assert.Equal(string.Empty, ReadString(reader));
        Assert.Equal(score.SubmittedAt.UtcDateTime.Ticks, reader.ReadInt64());
        Assert.Equal(frames, reader.ReadBytes(reader.ReadInt32()));
        Assert.Equal(1234L, reader.ReadInt64());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
    }

    private static string ReadString(BinaryReader reader)
    {
        if (reader.ReadByte() == 0)
            return string.Empty;
        int length = 0, shift = 0;
        byte b;
        do
        {
            b = reader.ReadByte();
            length |= (b & 0x7f) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }
}
