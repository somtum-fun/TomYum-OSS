// Stable spectator frame bundles, read for osu!lazer viewers (see StableSpectatePlugin.cs).

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace TomYum.Plugins.StableSpectate;

/// <summary>One replay frame: song time (ms), cursor (osu!pixels; mania keys in X), buttons.</summary>
/// <param name="Time">Song time in milliseconds.</param>
/// <param name="X">Cursor X (mania: the pressed keys as bits).</param>
/// <param name="Y">Cursor Y.</param>
/// <param name="Buttons">Pressed buttons (M1 1, M2 2, K1 4, K2 8, smoke 16).</param>
public sealed record StableFrame(int Time, float X, float Y, byte Buttons);

/// <summary>A score frame's counts, as osu!(stable) keeps them.</summary>
/// <param name="Count300">300s.</param>
/// <param name="Count100">100s.</param>
/// <param name="Count50">50s.</param>
/// <param name="CountGeki">Gekis (mania: MAX).</param>
/// <param name="CountKatu">Katus (mania: 200).</param>
/// <param name="CountMiss">Misses.</param>
/// <param name="TotalScore">Score.</param>
/// <param name="MaxCombo">Highest combo.</param>
/// <param name="Combo">Current combo.</param>
public sealed record StableScore(
    int Count300, int Count100, int Count50, int CountGeki, int CountKatu, int CountMiss,
    long TotalScore, int MaxCombo, int Combo);

/// <summary>A spectator frame bundle: its frames, the replay action and the score so far.</summary>
/// <param name="Frames">The frames, oldest first.</param>
/// <param name="Action">The replay action (0 play, 1 new song, 2 skip, 3 completion, 4 fail, 5 pause, 6 unpause, 7 song select, 8 watching other).</param>
/// <param name="Score">The score frame, or null when the bundle has none.</param>
public sealed record StableBundle(IReadOnlyList<StableFrame> Frames, byte Action, StableScore? Score);

/// <summary>
/// Reads a SPECTATE_FRAMES payload: extra (i32), frame count (u16), the frames (buttons u8, taiko u8, x f32,
/// y f32, time i32), the action (u8), then the score frame (time i32, id u8, six u16 counts, score i32, max
/// combo u16, combo u16, perfect, hp, tag, ...). Old builds' bundles differ only after the score frame's first
/// 28 bytes, which is all this reads.
/// </summary>
public static class StableFrames
{
    private const int FrameLength = 14;
    private const int ScoreFrameLength = 28;
    private const int MaxFrames = 4096;

    /// <summary>The bundle, or null when the payload isn't one.</summary>
    /// <param name="payload">The raw payload.</param>
    /// <returns>The bundle, or null.</returns>
    public static StableBundle? Read(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 7)
            return null;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        var actionAt = 6 + (count * FrameLength);
        if (count > MaxFrames || payload.Length <= actionAt)
            return null;

        var frames = new List<StableFrame>(count);
        for (var i = 0; i < count; i++)
        {
            var f = payload.Slice(6 + (i * FrameLength), FrameLength);
            var x = BinaryPrimitives.ReadSingleLittleEndian(f[2..]);
            var y = BinaryPrimitives.ReadSingleLittleEndian(f[6..]);
            if (!float.IsFinite(x) || !float.IsFinite(y))
                return null;
            frames.Add(new StableFrame(BinaryPrimitives.ReadInt32LittleEndian(f[10..]), x, y, f[0]));
        }

        var action = payload[actionAt];
        if (action > 8)
            return null;

        StableScore? score = null;
        var s = payload[(actionAt + 1)..];
        if (s.Length >= ScoreFrameLength)
        {
            score = new StableScore(
                Count300: BinaryPrimitives.ReadUInt16LittleEndian(s[5..]),
                Count100: BinaryPrimitives.ReadUInt16LittleEndian(s[7..]),
                Count50: BinaryPrimitives.ReadUInt16LittleEndian(s[9..]),
                CountGeki: BinaryPrimitives.ReadUInt16LittleEndian(s[11..]),
                CountKatu: BinaryPrimitives.ReadUInt16LittleEndian(s[13..]),
                CountMiss: BinaryPrimitives.ReadUInt16LittleEndian(s[15..]),
                TotalScore: BinaryPrimitives.ReadInt32LittleEndian(s[17..]),
                MaxCombo: BinaryPrimitives.ReadUInt16LittleEndian(s[21..]),
                Combo: BinaryPrimitives.ReadUInt16LittleEndian(s[23..]));
        }

        return new StableBundle(frames, action, score);
    }
}
