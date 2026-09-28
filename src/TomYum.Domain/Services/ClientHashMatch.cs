using TomYum.Domain.Repositories;

namespace TomYum.Domain.Services;

/// <summary>
/// Which of a login's hashes identify the machine (bancho.py's rules). Under Wine the adapter list
/// is the literal <c>runningunderwine</c> and the disk serial is unreliable, so only the uninstall
/// id counts; otherwise any of adapters, uninstall id or disk serial. Hashes that many unrelated
/// machines share (empty, <c>md5("0")</c> from a disk without a serial) never count.
/// </summary>
public static class ClientHashMatch
{
    /// <summary>The adapter string the client sends when it runs under Wine.</summary>
    public const string Wine = "runningunderwine";

    /// <summary>md5("") and md5("0"): what the client sends when there's nothing to hash.</summary>
    private static readonly HashSet<string> Meaningless = new(StringComparer.OrdinalIgnoreCase)
    {
        "d41d8cd98f00b204e9800998ecf8427e",
        "cfcd208495d565ef66e7dff9f98764da",
    };

    /// <summary>The query that finds other accounts on the same machine.</summary>
    public static ClientHashQuery For(string adaptersStr, string adaptersMd5, string uninstallMd5, string diskSignatureMd5)
    {
        var uninstall = Usable(uninstallMd5);
        if (string.Equals(adaptersStr, Wine, StringComparison.OrdinalIgnoreCase))
            return new ClientHashQuery(null, uninstall, null);
        return new ClientHashQuery(Usable(adaptersMd5), uninstall, Usable(diskSignatureMd5));
    }

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
