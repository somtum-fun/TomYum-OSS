using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>Players' IRC keys, made in the website's account settings (<c>osu_user_ircauth</c>).</summary>
public sealed class OsuWebIrcKeys
{
    private readonly OsuWebDbContext _db;

    public OsuWebIrcKeys(OsuWebDbContext db)
    {
        _db = db;
    }

    /// <summary><paramref name="userId"/>'s key, or null when they haven't made one.</summary>
    public async Task<string?> TokenAsync(int userId, CancellationToken cancellationToken = default) =>
        (await _db.Database.SqlQueryRaw<string>(
            "SELECT token AS Value FROM osu_user_ircauth WHERE user_id = {0} AND token IS NOT NULL", userId)
            .ToListAsync(cancellationToken).ConfigureAwait(false)) is [var token, ..] ? token : null;
}
