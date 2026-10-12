using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Beatmaps;

/// <summary>A submission step that can't go ahead: a status (403 not yours/ranked, 404 no such set, 422 anything else) and the reason.</summary>
public sealed class SubmissionException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Ids handed out for a submission.</summary>
/// <param name="SetId">The set's id.</param>
/// <param name="IsNew">Whether the set was created now.</param>
/// <param name="BeatmapIds">One per requested difficulty, in the order asked for.</param>
/// <param name="Remaining">How many more new sets the mapper may submit now.</param>
public sealed record SubmissionIds(int SetId, IReadOnlyList<int> BeatmapIds, bool IsNew, int Remaining);

/// <summary>A file of a set's current package, as lazer compares them.</summary>
public sealed record SubmittedFile(string Name, string Sha256);

/// <summary>
/// Beatmap submission from the game clients (the stable editor's <c>.osz2</c> upload and lazer's
/// submission service), the way osu! runs it: the mapper first gets ids for the set and each
/// difficulty, writes them into the files, then uploads the package, which
/// <see cref="CustomBeatmapImporter.SubmitAsync"/> checks against those ids and stores.
/// </summary>
/// <remarks>
/// Rules follow ppy's osu-server-beatmap-submission: only the owner changes a set, never once
/// ranked, qualified or loved; not while restricted or silenced; at most 128 difficulties; a limited
/// number of sets in progress (4, plus one per ranked set up to 8; supporters 8, up to 20). Sets
/// untouched for four weeks don't count, nor do ids handed out but never used for a day (they stay
/// taken all the same: a client may still have them, so no id is ever handed out twice).
/// </remarks>
public sealed class BeatmapSubmissions
{
    public const int MaxDifficulties = 128;

    private static readonly TimeSpan InactiveAfter = TimeSpan.FromDays(28);
    private static readonly TimeSpan UnusedIdsExpireAfter = TimeSpan.FromDays(1);

    private readonly ICustomBeatmapSetRepository _sets;
    private readonly IBeatmapRepository _beatmaps;
    private readonly CustomBeatmapStore _store;
    private readonly CustomBeatmapImporter _importer;
    private readonly RestrictedUsers _restricted;
    private readonly TimeProvider _time;
    private readonly OsuWebCustomSets? _website;
    private readonly OsuWebInterop? _interop;

    public BeatmapSubmissions(
        ICustomBeatmapSetRepository sets,
        IBeatmapRepository beatmaps,
        CustomBeatmapStore store,
        CustomBeatmapImporter importer,
        RestrictedUsers restricted,
        TimeProvider time,
        OsuWebCustomSets? website = null,
        OsuWebInterop? interop = null)
    {
        _sets = sets;
        _beatmaps = beatmaps;
        _store = store;
        _importer = importer;
        _restricted = restricted;
        _time = time;
        _website = website;
        _interop = interop;
    }

    /// <summary>
    /// Hands out ids: for a new set when <paramref name="setId"/> is null, else for the owner's set
    /// <paramref name="setId"/>. Each entry of <paramref name="difficulties"/> is an id the set already
    /// has (kept) or null (a new difficulty); the set's other ids are dropped. With
    /// <paramref name="strictIds"/> an id the set doesn't have is refused (lazer), else it's given a
    /// new one (the stable editor sends whatever its files say). A null <paramref name="target"/>
    /// keeps the set's status (pending for a new set); the stable editor chooses it last.
    /// </summary>
    public async Task<SubmissionIds> ReserveAsync(User user, int? setId, IReadOnlyList<int?> difficulties, int? target, bool strictIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(difficulties);
        CheckStanding(user);
        if (target is not (null or CustomBeatmapSet.Wip or CustomBeatmapSet.Pending))
            throw new SubmissionException(403, "That isn't a status a mapper can choose.");
        if (difficulties.Count < 1)
            throw new SubmissionException(422, "The beatmap set must contain at least one beatmap.");
        if (difficulties.Count > MaxDifficulties)
            throw new SubmissionException(422, Invariant($"The beatmap set cannot contain more than {MaxDifficulties} beatmaps."));

        await CustomBeatmapImporter.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var owned = await OwnedSetsAsync(user.Id, now, cancellationToken).ConfigureAwait(false);
            var remaining = Remaining(user, owned, now);

            CustomBeatmapSet? record = null;
            IReadOnlyList<int> slots = [];
            if (setId is { } id)
            {
                record = await OwnedSetAsync(user, id, cancellationToken).ConfigureAwait(false);
                slots = await _sets.GetSlotsAsync(id, cancellationToken).ConfigureAwait(false);
                // Coming back from four weeks without updates takes a free slot again, as a new set does.
                if (IsInactive(record, now) && remaining <= 0)
                    throw new SubmissionException(422, "This beatmap set hasn't been updated for four weeks, and you don't have a free submission slot to bring it back.");
            }
            else if (remaining <= 0)
            {
                throw new SubmissionException(422, Invariant($"You have reached your limit of beatmap sets in progress ({Quota(user, owned.Count(s => s.Ranked))}). Finish or wait out one of them first (they stop counting four weeks after their last update)."));
            }

            var kept = new HashSet<int>();
            foreach (var wanted in difficulties.OfType<int>())
            {
                if (slots.Contains(wanted))
                {
                    if (!kept.Add(wanted))
                        throw new SubmissionException(422, Invariant($"Beatmap {wanted} is listed twice."));
                }
                else if (strictIds)
                {
                    throw new SubmissionException(422, "One of the beatmaps to keep does not belong to the specified set.");
                }
            }

            var newSetId = setId ?? await _beatmaps.ReserveCustomSetIdAsync(cancellationToken).ConfigureAwait(false);
            var nextId = await _beatmaps.ReserveCustomBeatmapIdAsync(cancellationToken).ConfigureAwait(false);
            var ids = new List<int>(difficulties.Count);
            foreach (var wanted in difficulties)
                ids.Add(wanted is { } keep && kept.Contains(keep) ? keep : nextId++);
            if (newSetId > OsuWebBeatmaps.MaxId || nextId - 1 > OsuWebBeatmaps.MaxId)
                throw new SubmissionException(422, "The server is out of custom beatmap ids.");

            await _sets.SaveAsync(new CustomBeatmapSet
            {
                SetId = newSetId,
                OwnerId = user.Id,
                Target = target ?? record?.Target ?? CustomBeatmapSet.Pending,
                CreatedAt = record?.CreatedAt ?? now,
                UpdatedAt = now,
                UploadedAt = record?.UploadedAt,
            }, ids, cancellationToken).ConfigureAwait(false);

            return new SubmissionIds(newSetId, ids, record is null, Math.Max(0, remaining - (record is null ? 1 : 0)));
        }
        finally
        {
            CustomBeatmapImporter.Gate.Release();
        }
    }

    /// <summary>
    /// Packages being unpacked or put together at once (an <c>.osz2</c> decrypted, lazer's patch
    /// applied): each holds up to 100 MB, several times over.
    /// </summary>
    public const int MaxDecoding = 2;

    private static readonly SemaphoreSlim Decoding = new(MaxDecoding, MaxDecoding);

    /// <summary>
    /// Runs <paramref name="work"/> (turning an upload into a package) when one of the
    /// <see cref="MaxDecoding"/> places is free.
    /// </summary>
    public static async Task<T> DecodeAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await Decoding.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return work();
        }
        finally
        {
            Decoding.Release();
        }
    }

    /// <summary>
    /// Throws a <see cref="SubmissionException"/> unless <paramref name="user"/> may upload to set
    /// <paramref name="setId"/> now (in good standing, their set, not ranked): checked before an
    /// upload is unpacked, so nobody else's upload costs the server that work.
    /// </summary>
    public async Task CheckCanUploadAsync(User user, int setId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        CheckStanding(user);
        await OwnedSetAsync(user, setId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores a full package for the owner's set.</summary>
    public async Task<CustomUploadResult> UploadAsync(User user, int setId, byte[] osz, string client, CancellationToken cancellationToken = default)
    {
        await CheckCanUploadAsync(user, setId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await _importer.SubmitAsync(osz, setId, user.Id, client, cancellationToken).ConfigureAwait(false);
        }
        catch (CustomUploadException ex)
        {
            throw new SubmissionException(422, ex.Message);
        }
    }

    /// <summary>Changes some files of the owner's set (lazer sends only what changed) and stores the result.</summary>
    public async Task<CustomUploadResult> PatchAsync(User user, int setId, IReadOnlyDictionary<string, byte[]> changed, IReadOnlyCollection<string> deleted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(deleted);
        await CheckCanUploadAsync(user, setId, cancellationToken).ConfigureAwait(false);
        if (!_store.HasSet(setId))
            throw new SubmissionException(404, "This beatmap set has no files to change yet.");
        if (!changed.Keys.Concat(deleted).All(PackageNames.IsSafe))
            throw new SubmissionException(422, "Invalid filename detected");

        var patched = await DecodeAsync(() => Patch(_store.OszPath(setId), changed, deleted), cancellationToken).ConfigureAwait(false);
        return await UploadAsync(user, setId, patched, "osu!(lazer)", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The package at <paramref name="oszPath"/> with <paramref name="changed"/> files put in and <paramref name="deleted"/> ones left out.</summary>
    private static byte[] Patch(string oszPath, IReadOnlyDictionary<string, byte[]> changed, IReadOnlyCollection<string> deleted)
    {
        using var source = ZipFile.OpenRead(oszPath);
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                if (changed.ContainsKey(entry.FullName) || deleted.Contains(entry.FullName))
                    continue;
                var copy = target.CreateEntry(entry.FullName, CompressionLevel.Fastest);
                copy.LastWriteTime = entry.LastWriteTime;
                using var from = entry.Open();
                using var to = copy.Open();
                from.CopyTo(to);
            }
            foreach (var (name, bytes) in changed)
            {
                using var to = target.CreateEntry(name, CompressionLevel.Fastest).Open();
                to.Write(bytes);
            }
        }
        return output.ToArray();
    }

    /// <summary>The files of the set's current package with their SHA-256 (what lazer patches against); empty before the first upload.</summary>
    public IReadOnlyList<SubmittedFile> CurrentFiles(int setId)
    {
        if (!_store.HasSet(setId))
            return [];
        using var archive = ZipFile.OpenRead(_store.OszPath(setId));
        return archive.Entries.Where(e => e.FullName.Length > 0 && !e.FullName.EndsWith('/')).Select(e =>
        {
            using var stream = e.Open();
            return new SubmittedFile(e.FullName, Convert.ToHexString(SHA256.HashData(stream)));
        }).ToList();
    }

    /// <summary>
    /// The stable editor's last step: whether the set is finished (pending) or a work in progress,
    /// and its description. Returns the set's forum thread (0 when there's none).
    /// </summary>
    public async Task<int> DescribeAsync(User user, int setId, bool complete, string? description, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var record = await OwnedSetAsync(user, setId, cancellationToken).ConfigureAwait(false);
        var target = complete ? CustomBeatmapSet.Pending : CustomBeatmapSet.Wip;
        var now = _time.GetUtcNow().UtcDateTime;
        var slots = await _sets.GetSlotsAsync(setId, cancellationToken).ConfigureAwait(false);
        await _sets.SaveAsync(new CustomBeatmapSet
        {
            SetId = setId,
            OwnerId = record.OwnerId,
            Target = target,
            CreatedAt = record.CreatedAt,
            UpdatedAt = now,
            UploadedAt = record.UploadedAt,
        }, slots, cancellationToken).ConfigureAwait(false);

        if (_website is null || record.UploadedAt is null)
            return 0;
        await _website.SetUnrankedStatusAsync(setId, target, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(description) && _interop is { IsConfigured: true })
        {
            try
            {
                await _interop.SyncBeatmapsetExtrasAsync(setId, new BeatmapsetExtras(description, []), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // The set is fine without its description; the mapper can post it again.
            }
        }
        return await _website.ThreadIdAsync(setId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The set, if <paramref name="user"/> owns it and it can still change.</summary>
    public async Task<CustomBeatmapSet> OwnedSetAsync(User user, int setId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (await _sets.GetAsync(setId, cancellationToken).ConfigureAwait(false) is not { } record)
            throw new SubmissionException(404, "That beatmap set doesn't exist on this server.");
        if (record.OwnerId != user.Id)
            throw new SubmissionException(403, "That beatmap set isn't yours.");
        var maps = await _beatmaps.GetBySetAsync(setId, cancellationToken).ConfigureAwait(false);
        if (maps.Any(b => b.Status is RankedStatus.Ranked or RankedStatus.Approved or RankedStatus.Qualified or RankedStatus.Loved))
            throw new SubmissionException(403, "The beatmap set is ranked, qualified or loved, so it can't be changed.");
        return record;
    }

    private void CheckStanding(User user)
    {
        if (user.IsRestricted || _restricted.Contains(user.Id))
            throw new SubmissionException(422, "Your account is currently restricted.");
        if (user.SilenceEndUtc is { } silence && silence > _time.GetUtcNow())
            throw new SubmissionException(422, "You are unable to submit or update maps while silenced.");
    }

    private sealed record OwnedSet(CustomBeatmapSet Set, bool Ranked);

    /// <summary>The user's sets that count toward the quota: not ids handed out a day ago and never used.</summary>
    private async Task<List<OwnedSet>> OwnedSetsAsync(int userId, DateTime now, CancellationToken cancellationToken)
    {
        var owned = new List<OwnedSet>();
        foreach (var set in await _sets.ListByOwnerAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            if (set.UploadedAt is null && now - set.UpdatedAt > UnusedIdsExpireAfter)
                continue;
            var maps = await _beatmaps.GetBySetAsync(set.SetId, cancellationToken).ConfigureAwait(false);
            owned.Add(new OwnedSet(set, maps.Any(b => b.Status is RankedStatus.Ranked or RankedStatus.Approved or RankedStatus.Loved)));
        }
        return owned;
    }

    private static int Remaining(User user, List<OwnedSet> owned, DateTime now)
    {
        if (user.Privileges.HasFlag(UserPrivileges.Administrator))
            return int.MaxValue / 2;
        var active = owned.Count(s => !s.Ranked && !IsInactive(s.Set, now));
        return Math.Max(0, Quota(user, owned.Count(s => s.Ranked)) - active);
    }

    private static int Quota(User user, int ranked) =>
        user.Privileges.HasFlag(UserPrivileges.Supporter) ? 8 + Math.Min(12, ranked) : 4 + Math.Min(4, ranked);

    private static bool IsInactive(CustomBeatmapSet set, DateTime now) => now - set.UpdatedAt > InactiveAfter;

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
