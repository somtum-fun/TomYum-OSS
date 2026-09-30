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
    public Task RecordLoginAsync(int userId, string client, string result, string? ip, string? country, string? agent, CancellationToken cancellationToken = default) =>
        _db.Database.ExecuteSqlRawAsync(
            "INSERT INTO tomyum_login_history (user_id, client, result, ip, country, agent, created_at) VALUES ({0}, {1}, {2}, {3}, {4}, {5}, UTC_TIMESTAMP())",
            [userId, client, result, Trim(ip, 45), Trim(country?.ToUpperInvariant(), 2), Trim(agent, 255)], cancellationToken);

    /// <summary>At most <paramref name="length"/> characters, or NULL when empty.</summary>
    private static object Trim(string? value, int length) =>
        string.IsNullOrEmpty(value) ? DBNull.Value : value.Length > length ? value[..length] : value;
}
