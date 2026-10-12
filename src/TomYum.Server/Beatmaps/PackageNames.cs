using System.IO.Compression;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// File names inside beatmap packages (<c>.osz</c>, <c>.osz2</c>, lazer's patches). Clients unpack
/// them with <c>Path.Combine(folder, name)</c> (the 2015 stable client among them), so a name that
/// climbs out of the folder or is rooted would write anywhere on a player's disk: every package that
/// comes in is checked with this.
/// </summary>
public static class PackageNames
{
    /// <summary>
    /// The name with forward slashes and no empty parts, or null when it isn't allowed: empty, rooted
    /// (<c>/</c>, <c>\</c>), with a part made only of dots and spaces (<c>.</c>, <c>..</c>; Windows
    /// drops trailing dots and spaces), with a <c>:</c> (a drive or a stream) or a control character.
    /// </summary>
    public static string? Canonical(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        var slashed = name.Replace('\\', '/');
        if (slashed.StartsWith('/') || slashed.Any(c => c == ':' || char.IsControl(c)))
            return null;
        var parts = slashed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p.Trim(' ', '.').Length == 0))
            return null;
        return string.Join('/', parts);
    }

    /// <summary>Whether <paramref name="name"/> is allowed (<see cref="Canonical"/>).</summary>
    public static bool IsSafe(string? name) => Canonical(name) is not null;

    /// <summary>The archive's first entry whose name isn't allowed, or null when they all are.</summary>
    public static string? FirstUnsafe(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return archive.Entries.Select(e => e.FullName).FirstOrDefault(n => !IsSafe(n));
    }

    /// <summary>
    /// The stored custom sets with entries whose names aren't allowed (stored before uploads were
    /// checked), and those names. A set whose file can't be read is listed with its error.
    /// </summary>
    public static IReadOnlyList<(int SetId, IReadOnlyList<string> Names)> FindUnsafeSets(CustomBeatmapStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var found = new List<(int, IReadOnlyList<string>)>();
        foreach (var setId in store.SetIds().Order())
        {
            try
            {
                using var archive = ZipFile.OpenRead(store.OszPath(setId));
                var names = archive.Entries.Select(e => e.FullName).Where(n => !IsSafe(n)).ToList();
                if (names.Count > 0)
                    found.Add((setId, names));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                found.Add((setId, [$"(couldn't read the file: {ex.Message})"]));
            }
        }
        return found;
    }
}
