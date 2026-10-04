using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Performance;
using TomYum.Persistence;

namespace TomYum.Server.Beatmaps;

/// <summary>A custom set as osu!direct lists it.</summary>
public sealed record CustomSet(int SetId, string Artist, string Title, string Creator, RankedStatus Status, DateTimeOffset LastUpdate, IReadOnlyList<Beatmap> Maps);

/// <summary>
/// Finds custom (uploaded) sets for osu!direct, which otherwise only searches the beatmap mirror:
/// they're in TomYum's own beatmap table, and their difficulty settings in their <c>.osu</c> files.
/// </summary>
public sealed class CustomBeatmapCatalog
{
    private readonly IServiceScopeFactory _scopes;
    private readonly OsuFileCache? _files;

    public CustomBeatmapCatalog(IServiceScopeFactory scopes, OsuFileCache? files = null)
    {
        _scopes = scopes;
        _files = files;
    }

    /// <summary>
    /// Custom sets (with <paramref name="includeImported"/>, also the osu! sets this server has
    /// imported; with <paramref name="frozenOnly"/>, only the osu! sets staff gave a status here)
    /// whose artist, title, creator or difficulty names contain every word of <paramref name="text"/>,
    /// newest first.
    /// </summary>
    public async Task<IReadOnlyList<CustomSet>> SearchAsync(string? text, int? mode, int? apiStatus, int limit, bool includeImported = false, bool frozenOnly = false, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<TomYumDbContext>() is not { } db)
            return [];

        var query = db.Beatmaps.AsNoTracking();
        if (frozenOnly)
            query = query.Where(b => b.SetId < Beatmap.CustomIdFloor && b.StatusFrozen);
        else if (!includeImported)
            query = query.Where(b => b.SetId >= Beatmap.CustomIdFloor);
        if (mode is { } m and >= 0 and <= 3)
            query = query.Where(b => b.Mode == m);
        if (apiStatus is { } api)
        {
            var status = RankedStatusExtensions.FromOsuApi(api);
            query = query.Where(b => b.Status == status);
        }
        foreach (var word in (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8))
        {
            query = query.Where(b => b.Artist.Contains(word) || b.Title.Contains(word) || b.Creator.Contains(word) || b.Version.Contains(word));
        }

        var setIds = await query
            .GroupBy(b => b.SetId)
            .OrderByDescending(g => g.Max(b => b.LastUpdate))
            .Select(g => g.Key)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (setIds.Count == 0)
            return [];

        var maps = await db.Beatmaps.AsNoTracking().Where(b => setIds.Contains(b.SetId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return setIds.Select(id => ToSet(maps.Where(b => b.SetId == id).ToList())).ToList();
    }

    /// <summary>
    /// The statuses staff gave osu! sets here (any of the set's maps frozen), as the set shows them
    /// (its best), for the mirror's results: the mirror only knows osu!'s status.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, RankedStatus>> FrozenStatusesAsync(IReadOnlyCollection<int> setIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setIds);
        var ids = setIds.Where(id => id is > 0 and < Beatmap.CustomIdFloor).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<int, RankedStatus>();

        await using var scope = _scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<TomYumDbContext>() is not { } db)
            return new Dictionary<int, RankedStatus>();

        var frozen = await db.Beatmaps.AsNoTracking()
            .Where(b => ids.Contains(b.SetId) && b.StatusFrozen)
            .Select(b => b.SetId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (frozen.Count == 0)
            return new Dictionary<int, RankedStatus>();

        var maps = await db.Beatmaps.AsNoTracking().Where(b => frozen.Contains(b.SetId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return maps.GroupBy(b => b.SetId).ToDictionary(g => g.Key, g => ToSet(g.ToList()).Status);
    }

    /// <summary>The custom set with id <paramref name="setId"/>, or of beatmap <paramref name="beatmapId"/>, or of the map with checksum <paramref name="md5"/>.</summary>
    public async Task<CustomSet?> FindAsync(int? setId, int? beatmapId, string? md5, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<TomYumDbContext>() is not { } db)
            return null;

        var set = setId;
        if (set is null && (beatmapId is not null || md5 is not null))
        {
            set = await db.Beatmaps.AsNoTracking()
                .Where(b => beatmapId != null ? b.Id == beatmapId : b.Md5 == md5)
                .Select(b => (int?)b.SetId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        if (set is not { } found || found < Beatmap.CustomIdFloor)
            return null;

        var maps = await db.Beatmaps.AsNoTracking().Where(b => b.SetId == found).ToListAsync(cancellationToken).ConfigureAwait(false);
        return maps.Count == 0 ? null : ToSet(maps);
    }

    /// <summary>A set in osu!direct's search format (<c>{id}.osz|artist|title|…|diffs</c>).</summary>
    public string ToDirectLine(CustomSet set, bool withDifficulties = true)
    {
        ArgumentNullException.ThrowIfNull(set);
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{set.SetId}.osz|{Clean(set.Artist)}|{Clean(set.Title)}|{Clean(set.Creator)}|{ToApiStatus(set.Status)}|10.0|{set.LastUpdate.UtcDateTime:yyyy-MM-dd HH:mm:ss}|{set.SetId}|0|0|0|0|0");
        if (!withDifficulties)
            return line;
        var difficulties = set.Maps.OrderBy(b => b.Mode).ThenBy(b => b.StarRating).Select(b =>
        {
            var (cs, od, ar, hp) = Settings(b.Id);
            return string.Create(CultureInfo.InvariantCulture,
                $"[{b.StarRating:F2}⭐] {Clean(b.Version).Replace(",", " ", StringComparison.Ordinal)} {{cs: {cs} / od: {od} / ar: {ar} / hp: {hp}}}@{b.Mode}");
        });
        return line + "|" + string.Join(",", difficulties);
    }

    /// <summary>osu! API status numbers, which the client shows the status icon from.</summary>
    public static int ToApiStatus(RankedStatus status) => status switch
    {
        RankedStatus.Ranked => 1,
        RankedStatus.Approved => 2,
        RankedStatus.Qualified => 3,
        RankedStatus.Loved => 4,
        _ => 0,
    };

    private (double Cs, double Od, double Ar, double Hp) Settings(int beatmapId)
    {
        var path = _files is null ? null : Path.Combine(_files.CacheDirectory, beatmapId.ToString(CultureInfo.InvariantCulture) + ".osu");
        if (path is null || !File.Exists(path))
            return (0, 0, 0, 0);
        var file = OsuFileText.Parse(File.ReadAllBytes(path));
        var od = file.GetDouble("Difficulty", "OverallDifficulty") ?? 0;
        return (file.GetDouble("Difficulty", "CircleSize") ?? 0, od, file.GetDouble("Difficulty", "ApproachRate") ?? od, file.GetDouble("Difficulty", "HPDrainRate") ?? 0);
    }

    private static CustomSet ToSet(List<Beatmap> maps)
    {
        // The set shows its best status, like the website does.
        var status = maps.Select(b => b.Status).OrderByDescending(Rank).First();
        var first = maps.OrderBy(b => b.Id).First();
        return new CustomSet(first.SetId, first.Artist, first.Title, first.Creator, status, maps.Max(b => b.LastUpdate), maps);
    }

    private static int Rank(RankedStatus status) => status switch
    {
        RankedStatus.Ranked => 5,
        RankedStatus.Approved => 4,
        RankedStatus.Loved => 3,
        RankedStatus.Qualified => 2,
        _ => 0,
    };

    private static string Clean(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(c is '|' ? 'I' : c is '\n' or '\r' ? ' ' : c);
        return builder.ToString();
    }
}
