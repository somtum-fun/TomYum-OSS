using System.Globalization;
using System.Text;
using SharpCompress.Compressors.LZMA;
using TomYum.Bancho;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>Old clients' scores on today's leaderboards (docs/old-clients.md).</summary>
public class LegacyScoreRulesTests
{
    private static string[] Parts(string version)
    {
        var parts = new string[18];
        Array.Fill(parts, "0");
        parts[17] = version;
        return parts;
    }

    private static byte[] CompressLzma(string text)
    {
        var raw = Encoding.ASCII.GetBytes(text);
        using var output = new MemoryStream();
        using (var lzma = LzmaStream.Create(new LzmaEncoderProperties(), isLzma2: false, output))
        {
            output.Write(lzma.Properties, 0, lzma.Properties.Length);
            output.Write(BitConverter.GetBytes((long)raw.Length), 0, 8);
            lzma.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
    }

    [Theory]
    [InlineData("20140624", 2014, 6, 24)]
    [InlineData("20131231  ", 2013, 12, 31)] // padded with anti-cheat flag spaces
    [InlineData("b20150101", 2015, 1, 1)]
    public void TheBuild_IsTheScoresVersion(string version, int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), LegacyScoreRules.BuildOf(Parts(version)).Date);

    [Theory]
    [InlineData(15)] // b337–b476
    [InlineData(16)] // b497–b558
    [InlineData(17)] // b595b–b1218
    public void WithoutAVersion_ItsAnOldBuild(int fields)
    {
        var build = LegacyScoreRules.BuildOf(new string[fields]);
        Assert.True(build.IsLegacy);
        // The player's session says which, when they're online.
        Assert.Equal(1122, LegacyScoreRules.BuildOf(new string[fields], ClientBuild.Parse("b1122")).Number);
        // A session of today's client doesn't make the score today's.
        Assert.True(LegacyScoreRules.BuildOf(new string[fields], ClientBuild.Parse("b20240101")).IsLegacy);
    }

    [Theory]
    [InlineData("1553")]
    [InlineData("1700 ")]
    [InlineData("")]
    public void NumberedBuilds_AreOld(string version)
    {
        const int hiddenAndBit4 = (int)(Mods.Hidden | Mods.TouchDevice);
        var build = LegacyScoreRules.BuildOf(Parts(version));

        Assert.True(build.IsLegacy);
        Assert.Equal((int)Mods.Hidden, LegacyScoreRules.Mods(hiddenAndBit4, build));
    }

    [Fact]
    public void NoVideo_IsntTouchDevice_Before2014()
    {
        const int hiddenAndBit4 = (int)(Mods.Hidden | Mods.TouchDevice);

        Assert.Equal((int)Mods.Hidden, LegacyScoreRules.Mods(hiddenAndBit4, ClientBuild.Parse("b20131231")));
        Assert.Equal((int)Mods.Hidden, LegacyScoreRules.Mods(hiddenAndBit4, ClientBuild.Parse("b1122")));
        Assert.Equal(hiddenAndBit4, LegacyScoreRules.Mods(hiddenAndBit4, ClientBuild.Parse("b20140101")));
        Assert.Equal(hiddenAndBit4, LegacyScoreRules.Mods(hiddenAndBit4, ClientBuild.Parse("b20240101")));
    }

    [Fact]
    public void CoOp_IsRefused_FromOldClients()
    {
        Assert.True(LegacyScoreRules.IsRefused((int)Mods.KeyCoop, ClientBuild.Parse("b20150101")));
        Assert.False(LegacyScoreRules.IsRefused((int)Mods.Hidden, ClientBuild.Parse("b20150101")));
        // Today's clients are left to today's rules.
        Assert.False(LegacyScoreRules.IsRefused((int)Mods.KeyCoop, ClientBuild.Parse("b20240101")));
    }

    [Theory]
    // taiko: 300s + 100s + misses = the map's max combo in taiko
    [InlineData(1, 500, 20, 5, 525, true)]
    [InlineData(1, 500, 20, 5, 530, false)]
    // catch: fruits and droplets caught (300s, 100s) + misses
    [InlineData(2, 300, 80, 2, 382, true)]
    [InlineData(2, 300, 80, 2, 390, false)]
    // standard and mania aren't checked; an unknown max combo counts as a match
    [InlineData(0, 1, 1, 1, 999, true)]
    [InlineData(3, 1, 1, 1, 999, true)]
    [InlineData(1, 500, 20, 5, null, true)]
    public void Judgements_MustCoverTheMap_InTaikoAndCatch(int mode, int n300, int n100, int miss, int? maxCombo, bool expected) =>
        Assert.Equal(expected, LegacyScoreRules.JudgementsMatch(mode, n300, n100, miss, maxCombo));

    [Fact]
    public void TouchDevice_IsDetected_WhenCursorTeleportsOnClicks()
    {
        var sb = new StringBuilder("0|256|-500|0,100|100|100|0,");
        (int X, int Y)[] targets =
        [
            (100, 100), (320, 120), (140, 300), (380, 280), (120, 110), (360, 140),
            (180, 320), (400, 200), (130, 130), (350, 290), (150, 110), (390, 260),
        ];
        var (cx, cy) = targets[0];
        foreach (var (tx, ty) in targets)
        {
            // Finger in the air: cursor stays at previous touch point (cx, cy).
            for (var f = 0; f < 4; f++)
                sb.Append(CultureInfo.InvariantCulture, $"16|{cx}|{cy}|0,");
            // Finger touches down at (tx, ty) and holds for 3 frames.
            cx = tx;
            cy = ty;
            sb.Append(CultureInfo.InvariantCulture, $"16|{cx}|{cy}|1,16|{cx}|{cy}|1,16|{cx}|{cy}|1,");
        }
        sb.Append("-12345|0|0|12345,");

        var frames = sb.ToString();
        Assert.True(LegacyScoreRules.IsTouchDeviceFrames(frames));
        Assert.True(LegacyScoreRules.IsTouchDevice(CompressLzma(frames)));
    }

    [Fact]
    public void TouchDevice_IsDetected_ForTapXAndRelax()
    {
        // Tap+X: touch lands 1 frame before keyboard press (K1 = 5), > 10 jumps.
        var tapX = new StringBuilder("0|256|-500|0,100|100|100|0,");
        (int X, int Y)[] targets =
        [
            (100, 100), (340, 110), (130, 290), (390, 270), (110, 120), (350, 150),
            (170, 310), (410, 190), (140, 120), (360, 280), (160, 110), (380, 250),
        ];
        var (cx, cy) = targets[0];
        foreach (var (tx, ty) in targets)
        {
            tapX.Append(CultureInfo.InvariantCulture, $"16|{cx}|{cy}|0,16|{cx}|{cy}|0,");
            cx = tx;
            cy = ty;
            tapX.Append(CultureInfo.InvariantCulture, $"16|{cx}|{cy}|0,8|{cx}|{cy}|5,16|{cx}|{cy}|5,");
        }
        Assert.True(LegacyScoreRules.IsTouchDeviceFrames(tapX.ToString()));

        // Relax on touchscreen: no clicks, cursor stays still on notes and teleports in 1 frame between them.
        var relax = new StringBuilder("0|256|-500|0,100|100|100|0,");
        (cx, cy) = targets[0];
        foreach (var (tx, ty) in targets)
        {
            relax.Append(CultureInfo.InvariantCulture, $"16|{cx}|{cy}|0,16|{cx}|{cy}|0,16|{cx}|{cy}|0,");
            cx = tx;
            cy = ty;
            relax.Append(CultureInfo.InvariantCulture, $"16|{cx}|{cy}|0,16|{cx}|{cy}|0,");
        }
        Assert.True(LegacyScoreRules.IsTouchDeviceFrames(relax.ToString()));
    }

    [Fact]
    public void MouseAndTablet_AreNotFlaggedAsTouchDevice()
    {
        var sb = new StringBuilder("0|256|-500|0,100|100|100|0,");
        (float X, float Y)[] targets = [(100, 100), (320, 120), (140, 300), (380, 280), (120, 110), (360, 140), (180, 320), (400, 200)];
        var (cx, cy) = targets[0];
        foreach (var (tx, ty) in targets.Skip(1))
        {
            // Smooth 8-frame cursor movement from (cx, cy) to (tx, ty).
            for (var step = 1; step <= 8; step++)
            {
                var f = step / 8f;
                var x = cx + (tx - cx) * f;
                var y = cy + (ty - cy) * f;
                var keys = step == 8 ? 5 : 0;
                sb.Append(CultureInfo.InvariantCulture, $"16|{x:0.##}|{y:0.##}|{keys},");
            }
            cx = tx;
            cy = ty;
            sb.Append(CultureInfo.InvariantCulture, $"16|{cx:0.##}|{cy:0.##}|5,16|{cx:0.##}|{cy:0.##}|0,");
        }

        Assert.False(LegacyScoreRules.IsTouchDeviceFrames(sb.ToString()));
        Assert.False(LegacyScoreRules.IsTouchDevice(null));
        Assert.False(LegacyScoreRules.IsTouchDevice([1, 2, 3]));
    }
}
