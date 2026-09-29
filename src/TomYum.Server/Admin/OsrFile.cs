using System.Security.Cryptography;
using System.Text;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;

namespace TomYum.Server.Admin;

/// <summary>
/// A complete osu! replay file (.osr) from a score and its stored replay data (the LZMA frames the
/// stable client uploads), in the format osu! and lazer import.
/// </summary>
public static class OsrFile
{
    /// <summary>osu!'s game version in the file (a recent stable build).</summary>
    private const int GameVersion = 20250101;

    public static byte[] Build(Score score, string player, byte[] frames)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(frames);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((byte)GameMode.AsVanilla(score.Mode));
        writer.Write(GameVersion);
        WriteString(writer, score.BeatmapMd5);
        WriteString(writer, player);
        WriteString(writer, ReplayHash(score, player));
        writer.Write((ushort)score.Count300);
        writer.Write((ushort)score.Count100);
        writer.Write((ushort)score.Count50);
        writer.Write((ushort)score.CountGeki);
        writer.Write((ushort)score.CountKatu);
        writer.Write((ushort)score.CountMiss);
        writer.Write((int)Math.Min(score.ScoreValue, int.MaxValue));
        writer.Write((ushort)score.MaxCombo);
        writer.Write(score.Perfect);
        writer.Write(score.Mods);
        WriteString(writer, string.Empty); // life bar graph
        writer.Write(score.SubmittedAt.UtcDateTime.Ticks);
        writer.Write(frames.Length);
        writer.Write(frames);
        writer.Write(score.Id);
        writer.Flush();
        return output.ToArray();
    }

    /// <summary>osu!'s string: 0x00 for empty, else 0x0b, the length (ULEB128) and UTF-8.</summary>
    private static void WriteString(BinaryWriter writer, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            writer.Write((byte)0);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write((byte)0x0b);
        var length = (uint)bytes.Length;
        do
        {
            var b = (byte)(length & 0x7f);
            length >>= 7;
            writer.Write(length != 0 ? (byte)(b | 0x80) : b);
        }
        while (length != 0);
        writer.Write(bytes);
    }

    /// <summary>The replay's checksum as stable writes it (osu!'s format: MD5, not for security).</summary>
#pragma warning disable CA5351
    private static string ReplayHash(Score score, string player) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(
            $"{score.MaxCombo}osu{player}{score.BeatmapMd5}{score.ScoreValue}{score.Grade}")));
#pragma warning restore CA5351
}
