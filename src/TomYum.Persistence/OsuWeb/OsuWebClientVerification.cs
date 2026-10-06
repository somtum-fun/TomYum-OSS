using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// osu!stable devices a player has confirmed on the website, as osu.ppy.sh does: the website's
/// <c>osu_user_security</c> (its <c>/client-verifications</c> page marks one verified after the player
/// signs in there). A device is the client's uninstall id; an account's first device is trusted.
/// </summary>
public sealed class OsuWebClientVerification
{
    private readonly OsuWebDbContext _db;

    public OsuWebClientVerification(OsuWebDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Whether the player may log in from this device: it's verified, or it's the account's first one
    /// (then recorded as verified). Otherwise it's recorded unverified and false is returned, for the
    /// player to confirm it on the website. Hashes that aren't md5 hex can't be checked and are let in.
    /// </summary>
    public async Task<bool> IsVerifiedAsync(int userId, string osuMd5, string adaptersMd5, string uniqueMd5, string diskMd5, CancellationToken cancellationToken = default)
    {
        if (Md5(osuMd5) is not { } osu || Md5(uniqueMd5) is not { } unique || Md5(diskMd5) is not { } disk)
            return true;
        var mac = Md5(adaptersMd5) ?? new byte[16];

        var verified = await _db.Database.SqlQueryRaw<int>(
            "SELECT 1 AS Value FROM osu_user_security WHERE user_id = {0} AND unique_md5 = {1} AND verified = 1 LIMIT 1",
            userId, unique).AnyAsync(cancellationToken).ConfigureAwait(false);
        if (verified)
            return true;

        var known = await _db.Database.SqlQueryRaw<int>(
            "SELECT 1 AS Value FROM osu_user_security WHERE user_id = {0} LIMIT 1",
            userId).AnyAsync(cancellationToken).ConfigureAwait(false);

        // The account's first device is trusted; any other waits for the player to confirm it. A device
        // already waiting is stamped again: the website's verify page, opened without the client's
        // details, offers the account's device that tried most recently (in the last day).
        await _db.Database.ExecuteSqlRawAsync(
            "INSERT INTO osu_user_security (user_id, osu_md5, unique_md5, disk_md5, mac_md5, verified) VALUES ({0}, {1}, {2}, {3}, {4}, {5}) "
            + "ON DUPLICATE KEY UPDATE timestamp = CURRENT_TIMESTAMP",
            [userId, osu, unique, disk, mac, known ? 0 : 1], cancellationToken).ConfigureAwait(false);
        return !known;
    }

    /// <summary>
    /// Whether these hashes name a device that can be checked (md5 hex, as builds from 2012 on send them);
    /// <see cref="IsVerifiedAsync"/> lets any other in.
    /// </summary>
    /// <param name="osuMd5">The client's osu! path md5.</param>
    /// <param name="uniqueMd5">The client's uninstall id md5.</param>
    /// <param name="diskMd5">The client's disk signature md5.</param>
    public static bool IsDevice(string osuMd5, string uniqueMd5, string diskMd5) =>
        Md5(osuMd5) is not null && Md5(uniqueMd5) is not null && Md5(diskMd5) is not null;

    /// <summary>Whether the account has any device on record (verified or waiting).</summary>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> HasDevicesAsync(int userId, CancellationToken cancellationToken = default) =>
        await _db.Database.SqlQueryRaw<int>(
            "SELECT 1 AS Value FROM osu_user_security WHERE user_id = {0} LIMIT 1",
            userId).AnyAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Whether this device is still waiting for the player to confirm it on the website (a login from
    /// it was held and it isn't verified yet). Only reads: for the client's polling while it waits.
    /// </summary>
    public async Task<bool> IsPendingAsync(int userId, string uniqueMd5, CancellationToken cancellationToken = default)
    {
        if (Md5(uniqueMd5) is not { } unique)
            return false;
        var rows = await _db.Database.SqlQueryRaw<int>(
            "SELECT verified AS Value FROM osu_user_security WHERE user_id = {0} AND unique_md5 = {1}",
            userId, unique).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 && !rows.Contains(1);
    }

    private static byte[]? Md5(string hex)
    {
        if (hex.Length != 32)
            return null;
        var bytes = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                return null;
        }
        return bytes;
    }
}
