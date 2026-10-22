using System.Globalization;
using System.Text;
using SharpCompress.Compressors.LZMA;
using TomYum.Bancho;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// What keeps old clients' scores (docs/old-clients.md) fair on the leaderboards they share with
/// today's: score v1 and the mod multipliers haven't changed since, so their scores count as they
/// are, with these exceptions (osuTitanic's server does the same for the mods).
/// </summary>
public static class LegacyScoreRules
{
    /// <summary>Before 2014, mod bit 4 was NoVideo; today it's TouchDevice (which lowers pp).</summary>
    public static readonly DateOnly NoVideoUntil = new(2014, 1, 1);

    private const int ClickKeys = 1 | 2;
    private const double MinJumpDistance = 100.0;
    private const double MaxSettleDistance = 10.0;
    private const int MaxPressDelayMs = 100;
    private const int MinTouchJumps = 11; // osu!stable: jumpCount > 10.0
    private const long MaxDecompressedReplayBytes = 16 * 1024 * 1024;

    private readonly record struct ReplayPoint(int TimeDelta, int Time, float X, float Y, int Keys);

    /// <summary>
    /// The build that played the score, from the version at the end of the score string (field 18).
    /// Only builds before b1553 send fewer fields, and early ones send an empty version: those are old
    /// builds too (the player's session build when it's known, else an unknown numbered build).
    /// </summary>
    /// <param name="parts">The score string's fields.</param>
    /// <param name="session">The build the player logged in with, if they're online.</param>
    public static ClientBuild BuildOf(IReadOnlyList<string> parts, ClientBuild? session = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        // The version is padded with spaces (anti-cheat flags); "20140624", "b20140624" or "1553".
        var version = parts.Count >= 18 ? parts[17].Trim() : string.Empty;
        if (version.Length > 0)
            return ClientBuild.Parse(version.StartsWith('b') ? version : "b" + version);
        return session is { IsLegacy: true } ? session : UnknownNumbered;
    }

    /// <summary>A numbered build whose number isn't known (a score without a version).</summary>
    internal static readonly ClientBuild UnknownNumbered = new(string.Empty, null) { Number = 0 };

    /// <summary>The score's mods as today's numbers: NoVideo (bit 4 before 2014) isn't TouchDevice.</summary>
    /// <param name="mods">The score's mods.</param>
    /// <param name="build">The build that played the score.</param>
    public static int Mods(int mods, ClientBuild build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return build.IsBefore(NoVideoUntil) ? mods & ~(int)TomYum.Bancho.Mods.TouchDevice : mods;
    }

    /// <summary>
    /// Whether an osu! replay's LZMA-compressed frames show touchscreen cursor movement (single-frame
    /// teleports to notes while unpressed cursor frames stay still). Old builds never set
    /// <see cref="TomYum.Bancho.Mods.TouchDevice"/> themselves, and pre-2014 builds used bit 4 for NoVideo.
    /// </summary>
    /// <param name="compressedFrames">The LZMA-compressed replay frame stream uploaded with the score.</param>
    public static bool IsTouchDevice(byte[]? compressedFrames)
    {
        if (compressedFrames is not { Length: >= 13 })
            return false;

        try
        {
            var properties = compressedFrames[..5];
            var size = BitConverter.ToInt64(compressedFrames, 5);
            if (size is <= 0 or > MaxDecompressedReplayBytes)
                return false;

            using var input = new MemoryStream(compressedFrames, 13, compressedFrames.Length - 13, writable: false);
            using var lzma = LzmaStream.Create(properties, input, compressedFrames.Length - 13, size, leaveOpen: false);
            using var output = new MemoryStream((int)Math.Min(size, 64 * 1024));
            var buffer = new byte[8192];
            long totalRead = 0;
            int read;
            while ((read = lzma.Read(buffer, 0, buffer.Length)) > 0)
            {
                totalRead += read;
                if (totalRead > MaxDecompressedReplayBytes)
                    return false;
                output.Write(buffer, 0, read);
            }

            return IsTouchDeviceFrames(Encoding.ASCII.GetString(output.GetBuffer(), 0, (int)output.Length));
        }
#pragma warning disable CA1031 // corrupt or truncated replay bytes must never fail score submission
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>
    /// Whether decompressed <c>w|x|y|z</c> replay frames show touchscreen cursor movement, using
    /// osu!stable's <c>Player.UpdateTouchDeviceFlag</c> rule (<c>jump &gt; 100</c> osu!px followed by
    /// <c>&lt; 10</c> osu!px within <c>&lt; 100</c> ms of a press, <c>jumpCount &gt; 10</c>), adapted for
    /// 60 fps / 6 fps replay frames by requiring the cursor to be stationary (<c>&lt; 10</c> osu!px) before
    /// the jump so multi-frame mouse swipes aren't mistaken for single-tick teleports.
    /// </summary>
    /// <param name="frameText">The comma-separated <c>w|x|y|z</c> frame string.</param>
    public static bool IsTouchDeviceFrames(string? frameText)
    {
        if (string.IsNullOrEmpty(frameText))
            return false;

        var frames = ParseFrames(frameText);
        if (frames.Count < 20)
            return false;

        var hasClicks = false;
        for (var i = 0; i < frames.Count; i++)
        {
            if ((frames[i].Keys & ClickKeys) != 0)
            {
                hasClicks = true;
                break;
            }
        }

        var jumpCount = 0;
        var jumpFrame = false;
        var prevDist = 0.0;
        var lastPressTime = -1_000_000;
        var prevClick = frames[0].Keys & ClickKeys;

        for (var i = 1; i < frames.Count; i++)
        {
            var curr = frames[i];
            var prev = frames[i - 1];
            var click = curr.Keys & ClickKeys;
            if ((click & ~prevClick) != 0)
                lastPressTime = curr.Time;
            prevClick = click;

            var dist = Distance(prev.X, prev.Y, curr.X, curr.Y);
            if (jumpFrame)
            {
                var recentPress = hasClicks
                    ? curr.Time - lastPressTime is >= 0 and < MaxPressDelayMs
                    : curr.TimeDelta is > 0 and < MaxPressDelayMs;
                if (dist < MaxSettleDistance && recentPress)
                {
                    jumpCount++;
                    if (jumpCount >= MinTouchJumps)
                        return true;
                }

                jumpFrame = false;
            }

            if (dist > MinJumpDistance && prevDist < MaxSettleDistance && (hasClicks || curr.TimeDelta is > 0 and <= 35))
                jumpFrame = true;

            prevDist = dist;
        }

        return false;
    }

    private static List<ReplayPoint> ParseFrames(string text)
    {
        var points = new List<ReplayPoint>();
        var time = 0;
        Span<Range> fields = stackalloc Range[5];
        foreach (var range in text.AsSpan().Split(','))
        {
            var part = text.AsSpan(range);
            if (part.IsEmpty)
                continue;

            if (part.Split(fields, '|') < 4)
                continue;

            if (!int.TryParse(part[fields[0]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta)
                || delta == -12345
                || !float.TryParse(part[fields[1]], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !float.TryParse(part[fields[2]], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !int.TryParse(part[fields[3]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var keys))
                continue;

            time += delta;
            // Skip lead-in frames and osu!'s initial (256, -500) off-screen cursor seed.
            if (time < 0 || y < -100)
                continue;

            if (points.Count > 0 && points[^1].Time == time && (points[^1].Keys & ClickKeys) == (keys & ClickKeys))
                continue;

            var effectiveDelta = points.Count > 0 ? time - points[^1].Time : delta;
            points.Add(new ReplayPoint(effectiveDelta, time, x, y, keys));
        }

        return points;
    }

    private static double Distance(float x1, float y1, float x2, float y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Mods an old build's score is refused for: Co-op (made unranked in April 2015; old builds still send it).</summary>
    /// <param name="mods">The score's mods.</param>
    /// <param name="build">The build that played the score.</param>
    public static bool IsRefused(int mods, ClientBuild build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return build.IsLegacy && (mods & (int)TomYum.Bancho.Mods.KeyCoop) != 0;
    }

    /// <summary>
    /// Whether a passed play's judgements cover the map as today's client converts it. In taiko and
    /// catch every object that counts for combo is judged a 300, a 100 or a miss, so those add up to the
    /// map's max combo in that mode; an old client that converted the map differently doesn't match,
    /// and its score stays off the leaderboard. Other modes, or an unknown max combo, always match.
    /// </summary>
    /// <param name="mode">The ruleset.</param>
    /// <param name="count300">The 300 count.</param>
    /// <param name="count100">The 100 count.</param>
    /// <param name="countMiss">The miss count.</param>
    /// <param name="maxCombo">The map's max combo in that mode, if known.</param>
    public static bool JudgementsMatch(int mode, int count300, int count100, int countMiss, int? maxCombo) =>
        mode is not (1 or 2) || maxCombo is not { } expected || expected <= 0
        || count300 + count100 + countMiss == expected;

    /// <summary>For the log: the build's version as sent, or "unknown".</summary>
    /// <param name="build">The build that played the score.</param>
    public static string Describe(ClientBuild build) => build switch
    {
        { Date: { } date } => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        { Number: > 0 and var number } => string.Create(CultureInfo.InvariantCulture, $"b{number}"),
        { Number: 0 } => "a numbered build",
        _ => "unknown",
    };
}
