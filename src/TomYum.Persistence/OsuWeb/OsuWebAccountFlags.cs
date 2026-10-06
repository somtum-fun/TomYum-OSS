using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// Staff flags on an account and its login history, kept in the website's database (osu-web's
/// <c>tomyum_account_flags</c> and <c>tomyum_login_history</c>, written by its admin panel): a locked
/// account can't log in anywhere; a whitelisted one is left alone by anti-cheat.
/// </summary>
public sealed class OsuWebAccountFlags
{
    private readonly OsuWebDbContext _db;

    public OsuWebAccountFlags(OsuWebDbContext db)
    {
        _db = db;
    }

    public Task<bool> IsLockedAsync(int userId, CancellationToken cancellationToken = default) =>
        _db.Database.SqlQueryRaw<int>(
            "SELECT 1 AS Value FROM tomyum_account_flags WHERE user_id = {0} AND locked_at IS NOT NULL", userId)
            .AnyAsync(cancellationToken);

    public Task<bool> IsWhitelistedAsync(int userId, CancellationToken cancellationToken = default) =>
        _db.Database.SqlQueryRaw<int>(
            "SELECT 1 AS Value FROM tomyum_account_flags WHERE user_id = {0} AND whitelisted_at IS NOT NULL", userId)
            .AnyAsync(cancellationToken);

    /// <summary>Records a game login: <paramref name="result"/> is ok, held (a device to verify), locked or bad_password.</summary>
    public Task RecordLoginAsync(int userId, string client, string result, string? ip, string? country, string? agent, CancellationToken cancellationToken = default)
    {
        // Interpolated, so a missing value is a typed NULL parameter (a raw DBNull.Value has no type
        // mapping: a login without a country, say, wasn't recorded).
        string? trimmedIp = Trim(ip, 45), trimmedCountry = Trim(country?.ToUpperInvariant(), 2), trimmedAgent = Trim(agent, 255);
        return _db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO tomyum_login_history (user_id, client, result, ip, country, agent, created_at) VALUES ({userId}, {client}, {result}, {trimmedIp}, {trimmedCountry}, {trimmedAgent}, UTC_TIMESTAMP())",
            cancellationToken);
    }

    /// <summary>
    /// The stable client build of the player's last successful stable login at or before <paramref name="at"/>
    /// (UTC): 20260924 for b20260924.1, 1122 for the numbered b1122; null when none is known.
    /// </summary>
    public async Task<int?> StableBuildAtAsync(int userId, DateTime at, CancellationToken cancellationToken = default)
    {
        var agent = await _db.Database.SqlQueryRaw<string>(
            "SELECT agent AS Value FROM tomyum_login_history WHERE user_id = {0} AND client = 'stable' AND result = 'ok' AND created_at <= {1} AND agent IS NOT NULL ORDER BY created_at DESC, id DESC LIMIT 1",
            userId, at).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return BuildOf(agent);
    }

    /// <summary>The build number in a stable version string ("b20260924.1" is 20260924); null when it has none.</summary>
    public static int? BuildOf(string? version)
    {
        if (version is not { Length: > 1 } || version[0] != 'b')
            return null;
        var digits = version.AsSpan(1);
        var end = 0;
        while (end < digits.Length && char.IsAsciiDigit(digits[end]))
            end++;
        return end > 0 && int.TryParse(digits[..end], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var build) ? build : null;
    }

    /// <summary>At most <paramref name="length"/> characters, or NULL when empty.</summary>
    private static string? Trim(string? value, int length) =>
        string.IsNullOrEmpty(value) ? null : value.Length > length ? value[..length] : value;
}
