namespace TomYum.Domain.Services;

/// <summary>
/// A difficulty's file name the way osu! names it: <c>Artist - Title (Creator) [Version].osu</c>,
/// without the characters Windows doesn't allow in file names. osu!stable identifies an outdated
/// map by this name (its own copy's hash is unknown), and asks for the new file by it.
/// </summary>
public static class OsuFileName
{
    private static readonly char[] Forbidden = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    public static string For(string artist, string title, string creator, string version) =>
        Clean($"{artist} - {title} ({creator}) [{version}].osu");

    /// <summary>The name without forbidden characters (what a client's file is called).</summary>
    public static string Clean(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return string.Concat(name.Where(c => !Forbidden.Contains(c) && !char.IsControl(c)));
    }
}
