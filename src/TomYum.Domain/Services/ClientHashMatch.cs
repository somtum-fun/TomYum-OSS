using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Domain.Services;

/// <summary>
/// Which of a login's hashes identify the machine (bancho.py's rules), and how sure a match is.
/// Under Wine the adapter list is the literal <c>runningunderwine</c> and the disk serial is
/// unreliable, so only the uninstall id is looked up; otherwise adapters, uninstall id and disk
/// serial. Hashes that many unrelated machines share (empty, <c>md5("0")</c> from a disk without a
/// serial, Wine's usual values, or a value seen on <see cref="CommonMachines"/> or more different
/// machines) never count. A match is <see cref="ClientHashMatchStrength.Strong"/> when one login
/// shares two of the hashes (under Wine: the uninstall id and the osu! folder), else
/// <see cref="ClientHashMatchStrength.Weak"/>.
/// </summary>
public static class ClientHashMatch
{
    /// <summary>The adapter string the client sends when it runs under Wine.</summary>
    public const string Wine = "runningunderwine";

    /// <summary>md5("runningunderwine"): the adapters hash of every Wine login.</summary>
    public const string WineAdapters = "b4ec3c4334a0249dae95c284ec5983df";

    /// <summary>The disk serial hash Wine usually reports.</summary>
    public const string WineDisk = "ffae06fb022871fe9beb58b005c5e21d";

    /// <summary>A hash seen on this many different machines is too common to tell one apart.</summary>
    public const int CommonMachines = 5;

    /// <summary>md5("") and md5("0"): what the client sends when there's nothing to hash.</summary>
    private static readonly HashSet<string> Meaningless = new(StringComparer.OrdinalIgnoreCase)
    {
        "d41d8cd98f00b204e9800998ecf8427e",
        "cfcd208495d565ef66e7dff9f98764da",
        WineAdapters,
        WineDisk,
    };

    /// <summary>The query that finds other accounts on the same machine.</summary>
    public static ClientHashQuery For(string adaptersStr, string adaptersMd5, string uninstallMd5, string diskSignatureMd5)
    {
        var uninstall = Usable(uninstallMd5);
        if (IsWine(adaptersStr, adaptersMd5, diskSignatureMd5))
            return new ClientHashQuery(null, uninstall, null, IsWine: true);
        return new ClientHashQuery(Usable(adaptersMd5), uninstall, Usable(diskSignatureMd5));
    }

    /// <summary>Whether a login (or a stored combination, with an empty adapter list) came from Wine.</summary>
    public static bool IsWine(string? adaptersStr, string? adaptersMd5, string? diskSignatureMd5) =>
        string.Equals(adaptersStr, Wine, StringComparison.OrdinalIgnoreCase)
        || Normalize(adaptersMd5) == WineAdapters
        || Normalize(diskSignatureMd5) == WineDisk;

    /// <summary>
    /// How sure each other account found by <paramref name="query"/> is to be on this machine.
    /// <paramref name="rows"/> are every stored combination that shares one of the query's hashes
    /// (<see cref="IClientHashRepository.FindMatchingAsync"/>); the result keeps their accounts'
    /// order (most recent first) and leaves out accounts nothing counts for.
    /// </summary>
    public static IReadOnlyList<ClientHashMatchResult> Judge(ClientHashQuery query, string? osuPathMd5, IReadOnlyList<ClientHash> rows)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(rows);

        var path = Usable(osuPathMd5);
        // Too common: the hash turns up on many machines that differ in their other hardware.
        var adapters = query.Adapters is { } a && Machines(rows.Where(r => r.Adapters == a), r => r.DiskSerial) < CommonMachines ? a : null;
        var disk = query.DiskSerial is { } d && Machines(rows.Where(r => r.DiskSerial == d && !IsWineRow(r)), r => r.Adapters) < CommonMachines ? d : null;
        var uninstall = query.UninstallId is { } u && Machines(rows.Where(r => r.UninstallId == u), r => r.Adapters + r.DiskSerial) < CommonMachines ? u : null;

        var results = new List<ClientHashMatchResult>();
        foreach (var account in rows.GroupBy(r => r.UserId))
        {
            var best = 0;
            foreach (var row in account)
            {
                var wineRow = IsWineRow(row);
                var count = 0;
                if (adapters is not null && row.Adapters == adapters)
                    count++;
                if (disk is not null && !wineRow && row.DiskSerial == disk)
                    count++;
                if (uninstall is not null && row.UninstallId == uninstall)
                {
                    count++;
                    // Wine has nothing else to go on: the same install in the same folder.
                    if ((query.IsWine || wineRow) && path is not null && row.OsuPath == path)
                        count++;
                }
                best = Math.Max(best, count);
            }
            if (best > 0)
                results.Add(new ClientHashMatchResult(account.Key, best >= 2 ? ClientHashMatchStrength.Strong : ClientHashMatchStrength.Weak, best));
        }
        return results;
    }

    private static bool IsWineRow(ClientHash row) => IsWine(null, row.Adapters, row.DiskSerial);

    private static int Machines(IEnumerable<ClientHash> rows, Func<ClientHash, string> other) =>
        rows.Select(other).Where(h => h.Length > 0).Distinct(StringComparer.Ordinal).Count();

    /// <summary>A lower-case 32-character hex hash, or null when it isn't one or means nothing.</summary>
    public static string? Usable(string? hash)
    {
        var normalized = Normalize(hash);
        return normalized.Length == 32 && !Meaningless.Contains(normalized) ? normalized : null;
    }

    /// <summary>Lower-case, trimmed, cut to 32 characters (the stored width).</summary>
    public static string Normalize(string? hash)
    {
        var text = (hash ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length > 32)
            text = text[..32];
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c))
                return string.Empty;
        }
        return text;
    }
}

/// <summary>How sure a hardware match is.</summary>
public enum ClientHashMatchStrength
{
    /// <summary>One hash in common: maybe the same machine, maybe chance.</summary>
    Weak = 1,

    /// <summary>Two hashes in common on one login: the same machine.</summary>
    Strong = 2,
}

/// <summary>Another account that shares hashes with a login.</summary>
/// <param name="UserId">The other account.</param>
/// <param name="Strength">How sure the match is.</param>
/// <param name="Matched">How many hashes one of its logins shares.</param>
public sealed record ClientHashMatchResult(int UserId, ClientHashMatchStrength Strength, int Matched);
