using System.Collections.Concurrent;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// Resolves <see cref="Beatmap"/> metadata by MD5 hash, fronting the osu! v1 API
/// (<see cref="IOsuApiClient"/>) with an in-memory cache so repeat lookups of the same map are
/// free. Mirrors bancho.py's RAM-first resolution path; the DB tier and per-set cache-expiry
/// recheck cadence are deferred until <c>TomYum.Persistence</c> lands.
/// </summary>
/// <remarks>
/// A negative result (the API does not know the hash) is cached too — as a <c>null</c> sentinel —
/// so an unsubmitted map polled repeatedly by the client does not hammer the API. The cache holds
/// for the process lifetime for now; expiry/refresh is a later slice.
/// </remarks>
public sealed class BeatmapService
{
    private readonly IOsuApiClient _api;
    private readonly ConcurrentDictionary<string, Beatmap?> _byMd5 = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, byte> _fetchedSets = new();
    private readonly ConcurrentDictionary<int, RankedStatus> _statusOverrides = new();

    /// <summary>Creates the service over the osu! API client.</summary>
    public BeatmapService(IOsuApiClient api) => _api = api;

    /// <summary>Number of MD5 hashes currently cached (including negative not-found entries).</summary>
    public int CachedCount => _byMd5.Count;

    /// <summary>
    /// Makes beatmap <paramref name="beatmapId"/> report <paramref name="status"/> (a staff override,
    /// stored frozen in the database) instead of what the osu! API says — leaderboards and pp follow
    /// it. Applies to cached lookups immediately.
    /// </summary>
    public void SetStatusOverride(int beatmapId, RankedStatus status)
    {
        _statusOverrides[beatmapId] = status;
        foreach (var beatmap in _byMd5.Values)
        {
            if (beatmap?.Id == beatmapId)
                ApplyOverride(beatmap);
        }
    }

    /// <summary>The staff override for <paramref name="beatmapId"/>, if any.</summary>
    public RankedStatus? GetStatusOverride(int beatmapId) =>
        _statusOverrides.TryGetValue(beatmapId, out var status) ? status : null;

    private Beatmap? ApplyOverride(Beatmap? beatmap)
    {
        if (beatmap is not null && _statusOverrides.TryGetValue(beatmap.Id, out var status))
        {
            beatmap.Status = status;
            beatmap.StatusFrozen = true;
            beatmap.OsuApiStatus = null; // the API's value no longer describes this map
        }
        return beatmap;
    }

    /// <summary>
    /// Resolves a beatmap by its <c>.osu</c> MD5 hash. Returns <c>null</c> when the map is unknown
    /// to the API (treat as <see cref="Domain.Enums.RankedStatus.NotSubmitted"/>). Results — including
    /// the not-found result — are cached.
    /// </summary>
    public async Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(md5))
        {
            return null;
        }

        if (_byMd5.TryGetValue(md5, out var cached))
        {
            return cached;
        }

        var resolved = ApplyOverride(await _api.GetByMd5Async(md5, cancellationToken).ConfigureAwait(false));
        _byMd5[md5] = resolved;
        return resolved;
    }

    /// <summary>
    /// Fetches every difficulty of set <paramref name="setId"/> the first time it is asked for in
    /// this process, and returns empty on later calls — so the caller imports each set once per
    /// run instead of on every play. A failed or empty fetch is retried on the next call. Official
    /// sets only: server-local custom sets aren't on the osu! API.
    /// </summary>
    public async Task<IReadOnlyList<Beatmap>> FetchSetOnceAsync(int setId, CancellationToken cancellationToken = default)
    {
        if (!IsOfficialSet(setId) || !_fetchedSets.TryAdd(setId, 0))
        {
            return [];
        }

        var set = await FetchAndCacheAsync(setId, cancellationToken).ConfigureAwait(false);
        if (set.Count == 0)
        {
            _fetchedSets.TryRemove(setId, out _);
        }
        return set;
    }

    /// <summary>
    /// Fetches every difficulty of set <paramref name="setId"/> from the API now, whether or not it
    /// was fetched before (staff imports want current data). Empty for unknown or custom sets.
    /// </summary>
    public async Task<IReadOnlyList<Beatmap>> FetchSetAsync(int setId, CancellationToken cancellationToken = default)
    {
        if (!IsOfficialSet(setId))
        {
            return [];
        }

        var set = await FetchAndCacheAsync(setId, cancellationToken).ConfigureAwait(false);
        if (set.Count > 0)
        {
            _fetchedSets.TryAdd(setId, 0);
        }
        return set;
    }

    private static bool IsOfficialSet(int setId) => setId is > 0 and < Beatmap.CustomIdFloor;

    private async Task<IReadOnlyList<Beatmap>> FetchAndCacheAsync(int setId, CancellationToken cancellationToken)
    {
        var set = await _api.GetSetAsync(setId, cancellationToken).ConfigureAwait(false);

        // The set's difficulties are now known: later lookups of them skip the API.
        foreach (var beatmap in set)
        {
            _byMd5[beatmap.Md5] = ApplyOverride(beatmap);
        }
        return set;
    }
}
