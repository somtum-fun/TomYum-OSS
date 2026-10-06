using System.Buffers.Binary;

namespace TomYum.Bancho;

/// <summary>
/// Brings score frames and spectator frame bundles from old clients (docs/old-clients.md) up to
/// today's layout, so today's clients can read them: builds before b20151106 send score frames
/// without the ScoreV2 flag, and builds before b20160404 send frame bundles without the trailing
/// sequence number. Today's frames reach old clients as they are: those read what they know and
/// ignore what follows. Anything not in the expected old layout is returned unchanged.
/// Those dates are from Lekuruu's protocol notes and too late for some builds: b20140624 already sends
/// bundles in today's layout (seen 2026-10-02: 38 bytes in, 38 out), which pass through untouched.
/// The spectator frame handler logs, once per session, what an old build sends and what went out.
/// </summary>
public static class LegacyFrames
{
    /// <summary>A score frame without the ScoreV2 flag: time, id, six hit counts, score, combos, perfect, hp, tag.</summary>
    public const int OldScoreFrameLength = 28;

    /// <summary>One replay frame in a bundle: buttons, taiko byte, x, y, time.</summary>
    public const int ReplayFrameLength = 14;

    /// <summary>
    /// A match score frame (MATCH_SCORE_UPDATE) from <paramref name="sender"/> in today's layout: the
    /// ScoreV2 flag (off) added to an old 28-byte frame.
    /// </summary>
    public static byte[] ScoreFrame(ReadOnlySpan<byte> frame, ClientBuild sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (sender.HasScoreV2Frames || frame.Length != OldScoreFrameLength)
            return frame.ToArray();
        var upgraded = new byte[OldScoreFrameLength + 1];
        frame.CopyTo(upgraded);
        return upgraded; // last byte 0: no ScoreV2
    }

    /// <summary>
    /// A spectator frame bundle from <paramref name="sender"/> in today's layout: the ScoreV2 flag (off)
    /// after its score frame and <paramref name="sequence"/> at the end, where the build didn't send them.
    /// </summary>
    public static byte[] SpectateFrames(ReadOnlySpan<byte> bundle, ClientBuild sender, ushort sequence)
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (sender.HasFrameSequence || bundle.Length < 6)
            return bundle.ToArray();

        // extra (i32), frame count (u16), the frames, action (u8), then the score frame.
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bundle[4..]);
        var scoreFrameAt = 6 + (count * ReplayFrameLength) + 1;
        var scoreFrameLength = sender.HasScoreV2Frames ? bundle.Length - scoreFrameAt : OldScoreFrameLength;
        if (scoreFrameLength < OldScoreFrameLength || bundle.Length != scoreFrameAt + scoreFrameLength)
            return bundle.ToArray();

        var addFlag = !sender.HasScoreV2Frames;
        var upgraded = new byte[bundle.Length + (addFlag ? 1 : 0) + 2];
        bundle.CopyTo(upgraded);
        // With the flag added it's 0 (no ScoreV2); the sequence number goes last.
        BinaryPrimitives.WriteUInt16LittleEndian(upgraded.AsSpan(upgraded.Length - 2), sequence);
        return upgraded;
    }

    /// <summary>The highest replay action a bundle carries (WatchingOther).</summary>
    public const byte MaxReplayAction = 8;

    /// <summary>
    /// The replay action of a frame bundle in today's layout (what <see cref="SpectateFrames"/> returns):
    /// extra (i32), frame count (u16), the frames, then the action byte. Null when the bundle is too
    /// short for its frame count or the action isn't one osu! has.
    /// </summary>
    /// <param name="bundle">The bundle, in today's layout.</param>
    /// <returns>The action (0 play … 5 pause, 6 unpause …), or null.</returns>
    public static byte? ReadAction(ReadOnlySpan<byte> bundle)
    {
        if (bundle.Length < 7)
            return null;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bundle[4..]);
        var actionAt = 6 + (count * ReplayFrameLength);
        if (bundle.Length <= actionAt)
            return null;
        var action = bundle[actionAt];
        return action <= MaxReplayAction ? action : null;
    }
}
