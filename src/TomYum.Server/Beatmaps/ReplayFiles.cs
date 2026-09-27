using System.Globalization;

namespace TomYum.Server.Beatmaps;

/// <summary>Where replays are kept: <c>{Replays:Path}/{scoreId}.osr</c> (default <c>data/replays</c>).</summary>
public static class ReplayFiles
{
    public static string Directory(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration["Replays:Path"] ?? "data/replays";
    }

    /// <summary>The absolute path of score <paramref name="scoreId"/>'s replay (it may not exist).</summary>
    public static string PathFor(IConfiguration configuration, long scoreId) =>
        Path.GetFullPath(Path.Combine(Directory(configuration), string.Create(CultureInfo.InvariantCulture, $"{scoreId}.osr")));

    public static bool Exists(IConfiguration configuration, long scoreId) => File.Exists(PathFor(configuration, scoreId));
}
