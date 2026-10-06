using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// Reads and edits the text of a <c>.osu</c> file the way uploads need: the <c>key:value</c> fields
/// of its sections, the background from <c>[Events]</c>, and giving it a map and set id. Everything
/// else in the file is kept byte for byte, since the client identifies a map by the file's MD5.
/// </summary>
public sealed class OsuFileText
{
    private readonly List<string> _lines;
    private readonly string _newline;

    private OsuFileText(List<string> lines, string newline)
    {
        _lines = lines;
        _newline = newline;
    }

    /// <summary>Reads a <c>.osu</c> file (UTF-8, with or without BOM).</summary>
    public static OsuFileText Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return new OsuFileText(text.Split('\n').Select(l => l.TrimEnd('\r')).ToList(), newline);
    }

    /// <summary>Whether this looks like an osu! beatmap (starts with the format line).</summary>
    public bool IsBeatmap => _lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim().StartsWith("osu file format", StringComparison.Ordinal) == true;

    /// <summary>A <c>key:value</c> field of a section (e.g. <c>Metadata</c>, <c>Version</c>), or null.</summary>
    public string? Get(string section, string key)
    {
        var (start, end) = SectionRange(section);
        for (var i = start; i < end; i++)
        {
            var colon = _lines[i].IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && _lines[i][..colon].Trim().Equals(key, StringComparison.Ordinal))
                return _lines[i][(colon + 1)..].Trim();
        }
        return null;
    }

    public int GetInt(string section, string key, int fallback = 0) =>
        int.TryParse(Get(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public double? GetDouble(string section, string key) =>
        double.TryParse(Get(section, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Sets a field in a section, adding it (and the section) when missing.</summary>
    public void Set(string section, string key, string value)
    {
        var (start, end) = SectionRange(section);
        if (start < 0)
        {
            _lines.Add(string.Empty);
            _lines.Add($"[{section}]");
            _lines.Add($"{key}:{value}");
            return;
        }
        for (var i = start; i < end; i++)
        {
            var colon = _lines[i].IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && _lines[i][..colon].Trim().Equals(key, StringComparison.Ordinal))
            {
                _lines[i] = $"{key}:{value}";
                return;
            }
        }
        // After the section's last field (before any blank lines that end it).
        var at = end;
        while (at > start && _lines[at - 1].Trim().Length == 0)
            at--;
        _lines.Insert(at, $"{key}:{value}");
    }

    /// <summary>The background image's file name from <c>[Events]</c> (<c>0,0,"bg.jpg",0,0</c>), or null.</summary>
    public string? Background
    {
        get
        {
            var (start, end) = SectionRange("Events");
            for (var i = start; i < end; i++)
            {
                var parts = _lines[i].Split(',');
                if (parts.Length >= 3 && parts[0].Trim() is "0" or "Background" && parts[1].Trim() == "0")
                    return parts[2].Trim().Trim('"').Replace('\\', '/');
            }
            return null;
        }
    }

    /// <summary>
    /// Hit objects: circles, sliders, spinners (mania holds count with spinners), and the first and
    /// last object's time in milliseconds (0 when there are none).
    /// </summary>
    public (int Circles, int Sliders, int Spinners, int FirstMs, int LastMs) HitObjects
    {
        get
        {
            var (start, end) = SectionRange("HitObjects");
            int circles = 0, sliders = 0, spinners = 0, first = -1, last = 0;
            for (var i = start; i < end; i++)
            {
                var parts = _lines[i].Split(',');
                if (parts.Length < 4 || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var type)
                    || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var time))
                    continue;
                if ((type & 1) != 0)
                    circles++;
                else if ((type & 2) != 0)
                    sliders++;
                else if ((type & (8 | 128)) != 0)
                    spinners++;
                // Spinners and holds end later than they start (the field after the hit sound).
                var endTime = time;
                if ((type & (8 | 128)) != 0 && parts.Length > 5
                    && int.TryParse(parts[5].Split(':')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectEnd))
                    endTime = Math.Max(time, objectEnd);
                if (first < 0)
                    first = time;
                last = Math.Max(last, endTime);
            }
            return (circles, sliders, spinners, Math.Max(0, first), last);
        }
    }

    /// <summary>Total length of the breaks in <c>[Events]</c>, in milliseconds.</summary>
    public int BreakMs
    {
        get
        {
            var (start, end) = SectionRange("Events");
            var total = 0;
            for (var i = start; i < end; i++)
            {
                var parts = _lines[i].Split(',');
                if (parts.Length >= 3 && parts[0].Trim() is "2" or "Break"
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var from)
                    && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var to) && to > from)
                    total += to - from;
            }
            return total;
        }
    }

    /// <summary>Whether <c>[Events]</c> has a video, or storyboard sprites of its own.</summary>
    public (bool Video, bool Storyboard) EventKinds
    {
        get
        {
            var (start, end) = SectionRange("Events");
            bool video = false, storyboard = false;
            for (var i = start; i < end; i++)
            {
                var kind = _lines[i].Split(',')[0].Trim();
                video |= kind is "1" or "Video";
                storyboard |= kind is "Sprite" or "Animation" or "4" or "6";
            }
            return (video, storyboard);
        }
    }

    /// <summary>
    /// The map's BPM as osu! and lazer give it: the one it's at the longest (each uninherited timing point lasting until the next,
    /// the last until the last object; the first from the start), or null without timing points.
    /// </summary>
    public double? Bpm
    {
        get
        {
            var points = new List<(double Time, double BeatLength)>();
            var (start, end) = SectionRange("TimingPoints");
            for (var i = start; i < end; i++)
            {
                var parts = _lines[i].Split(',');
                if (parts.Length < 2
                    || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time)
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var beatLength)
                    || beatLength <= 0)
                    continue;
                // Uninherited unless the 7th field says 0.
                if (parts.Length >= 7 && parts[6].Trim() == "0")
                    continue;
                points.Add((time, beatLength));
            }
            if (points.Count == 0)
                return null;

            var lastTime = Math.Max(LastObjectTime ?? 0, points[^1].Time);
            var mostCommon = points
                .Select((p, i) =>
                {
                    if (p.Time > lastTime)
                        return (p.BeatLength, Duration: 0.0);
                    var from = i == 0 ? 0 : p.Time;
                    var to = i == points.Count - 1 ? lastTime : points[i + 1].Time;
                    return (p.BeatLength, Duration: to - from);
                })
                .GroupBy(p => Math.Round(p.BeatLength, 3))
                .OrderByDescending(g => g.Sum(p => p.Duration))
                .First().Key;
            return Math.Round(60000 / mostCommon, 2);
        }
    }

    /// <summary>The last hit object's start time (ms), or null without objects.</summary>
    public double? LastObjectTime
    {
        get
        {
            var (start, end) = SectionRange("HitObjects");
            for (var i = end - 1; i >= start && i >= 0; i--)
            {
                var parts = _lines[i].Split(',');
                if (parts.Length >= 3 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
                    return time;
            }
            return null;
        }
    }

    /// <summary>The file as UTF-8 bytes (no BOM), with its original line endings.</summary>
    public byte[] ToBytes() => Encoding.UTF8.GetBytes(string.Join(_newline, _lines));

    /// <summary>The MD5 the client identifies the map by.</summary>
    // CA5351: MD5 is how osu! identifies beatmap files, not a security measure.
#pragma warning disable CA5351
    public static string Md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));
#pragma warning restore CA5351

    /// <summary>Lines [first field, end) of a section; (-1, -1) when it's missing.</summary>
    private (int Start, int End) SectionRange(string section)
    {
        var header = $"[{section}]";
        var start = _lines.FindIndex(l => l.Trim().Equals(header, StringComparison.Ordinal));
        if (start < 0)
            return (-1, -1);
        var end = start + 1;
        while (end < _lines.Count && !(_lines[end].TrimStart().StartsWith('[') && _lines[end].TrimEnd().EndsWith(']')))
            end++;
        return (start + 1, end);
    }
}
