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

    /// <summary>
    /// A lazer play's replay as the spectator server saved it (a complete .osr, named by its website
    /// score id, in <c>Circleguard:LazerReplaysPath</c>); null when lazer replays aren't set up here.
    /// </summary>
    public static string? LazerPathFor(IConfiguration configuration, ulong osuWebScoreId)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var directory = configuration["Circleguard:LazerReplaysPath"];
        return string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, osuWebScoreId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Where <paramref name="score"/>'s replay is, if it has one: TomYum's file for stable plays (the
    /// replay data only: <see cref="ReplayFile.Complete"/> is false), the spectator's for lazer ones.
    /// </summary>
    public static ReplayFile? Find(IConfiguration configuration, TomYum.Domain.Entities.Score score)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (score.FromLazer)
            return score.OsuWebScoreId is { } webId && LazerPathFor(configuration, webId) is { } lazer && File.Exists(lazer)
                ? new ReplayFile(lazer, Complete: true)
                : null;
        return Exists(configuration, score.Id) ? new ReplayFile(PathFor(configuration, score.Id), Complete: false) : null;
    }
}

/// <summary>A replay on disk: a complete .osr, or the replay data only (the header comes from the score).</summary>
public sealed record ReplayFile(string Path, bool Complete);
