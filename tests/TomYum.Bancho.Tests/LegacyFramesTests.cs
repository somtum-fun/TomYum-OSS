using System.Buffers.Binary;
using TomYum.Bancho;
using Xunit;

namespace TomYum.Bancho.Tests;

/// <summary>Old clients' score frames and spectator bundles in today's layout (docs/old-clients.md).</summary>
public class LegacyFramesTests
{
    private static readonly ClientBuild Old = ClientBuild.Parse("b20140624");
    private static readonly ClientBuild Late2015 = ClientBuild.Parse("b20151201");
    private static readonly ClientBuild Today = ClientBuild.Parse("b20240123");

    private static byte[] OldScoreFrame()
    {
        var frame = new byte[LegacyFrames.OldScoreFrameLength];
        for (var i = 0; i < frame.Length; i++)
            frame[i] = (byte)(i + 1);
        return frame;
    }

    private static byte[] Bundle(int frames, byte[] scoreFrame, bool withSequence)
    {
        var bundle = new List<byte>();
        bundle.AddRange(BitConverter.GetBytes(0)); // extra
        bundle.AddRange(BitConverter.GetBytes((ushort)frames));
        bundle.AddRange(new byte[frames * LegacyFrames.ReplayFrameLength]);
        bundle.Add(0); // action
        bundle.AddRange(scoreFrame);
        if (withSequence)
            bundle.AddRange(BitConverter.GetBytes((ushort)7));
        return [.. bundle];
    }

    [Fact]
    public void AnOldScoreFrame_GetsTheScoreV2Flag()
    {
        var upgraded = LegacyFrames.ScoreFrame(OldScoreFrame(), Old);

        Assert.Equal(29, upgraded.Length);
        Assert.Equal(OldScoreFrame(), upgraded[..28]);
        Assert.Equal(0, upgraded[28]);
    }

    [Fact]
    public void TodaysScoreFrame_IsLeftAlone()
    {
        var frame = new byte[29];
        Assert.Equal(frame, LegacyFrames.ScoreFrame(frame, Today));
        // An old build with an unexpected length isn't touched either.
        Assert.Equal(30, LegacyFrames.ScoreFrame(new byte[30], Old).Length);
    }

    [Fact]
    public void AnOldBundle_GetsTheFlagAndASequenceNumber()
    {
        var bundle = Bundle(3, OldScoreFrame(), withSequence: false);

        var upgraded = LegacyFrames.SpectateFrames(bundle, Old, sequence: 42);

        Assert.Equal(bundle.Length + 3, upgraded.Length);
        Assert.Equal(bundle, upgraded[..bundle.Length]);
        Assert.Equal(0, upgraded[bundle.Length]); // no ScoreV2
        Assert.Equal(42, BinaryPrimitives.ReadUInt16LittleEndian(upgraded.AsSpan(upgraded.Length - 2)));
    }

    [Fact]
    public void ALate2015Bundle_OnlyGetsTheSequenceNumber()
    {
        var scoreFrame = new byte[29]; // already has the flag (off)
        var bundle = Bundle(2, scoreFrame, withSequence: false);

        var upgraded = LegacyFrames.SpectateFrames(bundle, Late2015, sequence: 5);

        Assert.Equal(bundle.Length + 2, upgraded.Length);
        Assert.Equal(5, BinaryPrimitives.ReadUInt16LittleEndian(upgraded.AsSpan(upgraded.Length - 2)));
    }

    [Fact]
    public void TodaysOrMalformedBundles_AreLeftAlone()
    {
        var today = Bundle(1, new byte[29], withSequence: true);
        Assert.Equal(today, LegacyFrames.SpectateFrames(today, Today, 1));

        var wrongLength = Bundle(1, OldScoreFrame(), withSequence: false)[..^3];
        Assert.Equal(wrongLength, LegacyFrames.SpectateFrames(wrongLength, Old, 1));
    }
}
