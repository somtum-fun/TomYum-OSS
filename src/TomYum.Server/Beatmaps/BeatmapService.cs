using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.Memory;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// Resolves <see cref="Beatmap"/> metadata by MD5 hash, fronting the osu! v1 API
/// (<see cref="IOsuApiClient"/>) with an in-memory cache so repeat lookups of the same map are
/// free. Mirrors bancho.py's RAM-first resolution path; the DB tier and per-set cache-expiry
/// recheck cadence are deferred until <c>TomYum.Persistence</c> lands.
/// </summary>
/// <remarks>
/// A negative result (the API does not know the hash) is cached too — as a <c>null</c> sentinel —
/// so an unsubmitted map polled repeatedly by the client does not hammer the API. Clients send any
/// string as a checksum, so only real ones (32 hex digits) are looked up at all, and the cache is
/// bounded (<see cref="MaxCached"/> entries; maps kept a day after their last use, misses an hour).
/// </remarks>
public sealed class BeatmapService : IDisposable
{
    /// <summary>Checksums cached at most.</summary>
    public const int MaxCached = 200_000;

    private static readonly TimeSpan FoundKept = TimeSpan.FromDays(1);
    private static readonly TimeSpan MissKept = TimeSpan.FromHours(1);

    private readonly IOsuApiClient _api;
    // Keys are lowercase checksums; values wrap the map so a miss (null) can be cached too.
    private readonly MemoryCache _byMd5 = new(new MemoryCacheOptions { SizeLimit = MaxCached });
    private readonly ConcurrentDictionary<int, byte> _fetchedSets = new();
    private readonly ConcurrentDictionary<int, RankedStatus> _statusOverrides = new();

    /// <summary>Creates the service over the osu! API client.</summary>
    private readonly IServiceScopeFactory? _scopes;

    /// <param name="api">The osu! API.</param>
    /// <param name="scopes">When given, maps the API doesn't know are looked up in this server's own
    /// beatmaps too: custom (uploaded) sets exist only there.</param>
    public BeatmapService(IOsuApiClient api, IServiceScopeFactory? scopes = null)
    {
        _api = api;
        _scopes = scopes;
    }

    public void Dispose() => _byMd5.Dispose();

    /// <summary>Number of MD5 hashes currently cached (including negative not-found entries).</summary>
    public int CachedCount => _byMd5.Count;

    /// <summary>Whether <paramref name="md5"/> looks like a checksum (32 hex digits).</summary>
    public static bool IsMd5(string? md5) => md5 is { Length: 32 } && md5.All(char.IsAsciiHexDigit);

    private sealed record Cached(Beatmap? Beatmap);

    private void Cache(string md5, Beatmap? beatmap)
    {
        if (!IsMd5(md5))
            return;
        using var entry = _byMd5.CreateEntry(md5.ToLowerInvariant());
        entry.Value = new Cached(beatmap);
        entry.Size = 1;
        if (beatmap is null)
            entry.AbsoluteExpirationRelativeToNow = MissKept;
        else
            entry.SlidingExpiration = FoundKept;
    }

    /// <summary>The cached checksums and their maps (misses have none).</summary>
    private IEnumerable<(string Md5, Beatmap? Beatmap)> CachedEntries()
    {
        foreach (var key in _byMd5.Keys)
        {
            if (key is string md5 && _byMd5.TryGetValue(md5, out Cached? cached) && cached is not null)
                yield return (md5, cached.Beatmap);
        }
    }

    /// <summary>
    /// Makes beatmap <paramref name="beatmapId"/> report <paramref name="status"/> (a staff override,
    /// stored frozen in the database) instead of what the osu! API says — leaderboards and pp follow
    /// it. Applies to cached lookups immediately.
    /// </summary>
    public void SetStatusOverride(int beatmapId, RankedStatus status)
    {
        _statusOverrides[beatmapId] = status;
        foreach (var (_, beatmap) in CachedEntries())
        {
            if (beatmap?.Id == beatmapId)
                ApplyOverride(beatmap);
        }
    }

    /// <summary>The staff override for <paramref name="beatmapId"/>, if any.</summary>
    public RankedStatus? GetStatusOverride(int beatmapId) =>
        _statusOverrides.TryGetValue(beatmapId, out var status) ? status : null;

    /// <summary>
    /// Drops the staff override for <paramref name="beatmapId"/>. Cached lookups of it are forgotten,
    /// so the next one (or a <see cref="FetchSetAsync"/>) takes the osu! API's status again.
    /// </summary>
    public void ClearStatusOverride(int beatmapId)
    {
        _statusOverrides.TryRemove(beatmapId, out _);
        foreach (var (md5, beatmap) in CachedEntries().ToList())
        {
            if (beatmap?.Id == beatmapId)
                _byMd5.Remove(md5);
        }
    }

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
    /// Resolves a beatmap by its <c>.osu</c> MD5 hash: the osu! API's map, or else a custom map of
    /// this server's. Returns <c>null</c> when neither knows it (treat as
    /// <see cref="Domain.Enums.RankedStatus.NotSubmitted"/>). Results — including the not-found
    /// result — are cached, but not a failed osu! lookup (osu! didn't answer), which is asked again.
    /// Anything but a checksum (<see cref="IsMd5"/>) is no map, without asking.
    /// </summary>
    public async Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default)
    {
        if (!IsMd5(md5))
        {
            return null;
        }

        md5 = md5.ToLowerInvariant();
        if (_byMd5.TryGetValue(md5, out Cached? cached) && cached is not null)
        {
            return cached.Beatmap;
        }

        var failed = new StrongBox<bool>();
        Beatmap? fromApi;
        using (OsuApiFailures.Watch(failed))
            fromApi = await _api.GetByMd5Async(md5, cancellationToken).ConfigureAwait(false);
        var resolved = ApplyOverride(fromApi ?? await GetCustomAsync(md5, cancellationToken).ConfigureAwait(false));
        // osu! not answering isn't osu! not knowing the map: ask again next time instead of
        // remembering it as unsubmitted.
        if (resolved is not null || !failed.Value)
            Cache(md5, resolved);
        return resolved;
    }

    /// <summary>
    /// Takes custom maps just stored (an upload) instead of what was cached for their checksums, so a
    /// client that asked before the upload sees them now.
    /// </summary>
    public void Remember(IEnumerable<Beatmap> beatmaps)
    {
        ArgumentNullException.ThrowIfNull(beatmaps);
        foreach (var beatmap in beatmaps)
        {
            foreach (var (md5, cached) in CachedEntries().ToList())
            {
                if (cached?.Id == beatmap.Id)
                    _byMd5.Remove(md5);
            }
            Cache(beatmap.Md5, ApplyOverride(beatmap));
        }
    }

    private async Task<Beatmap?> GetCustomAsync(string md5, CancellationToken cancellationToken)
    {
        if (_scopes is null)
            return null;
        await using var scope = _scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IBeatmapRepository>() is not { } repository)
            return null;
        var stored = await repository.GetByMd5Async(md5, cancellationToken).ConfigureAwait(false);
        return stored is { IsCustom: true } ? stored : null;
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
            Cache(beatmap.Md5, ApplyOverride(beatmap));
        }
        return set;
    }
}
