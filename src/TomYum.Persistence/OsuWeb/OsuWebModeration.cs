using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>What a moderation action did.</summary>
public enum ModerationOutcome
{
    Done,
    UnknownUser,
    AlreadyRestricted,
    NotRestricted,
    NotSilenced,
}

/// <summary>
/// Restrictions and silences on osu-web's account tables, written exactly like the website's GMT
/// panel writes them, so the in-game commands and the panel are interchangeable: a restriction sets
/// <c>user_warnings</c> and adds a permanent restriction row; lifting it clears the flag and adds a
/// note; a silence is a silence row with a length, and lifting one early adds a zero-length silence
/// row (osu-web's <c>User::isSilenced</c> reads the newest). Applying it in-game is the caller's job.
/// </summary>
public sealed class OsuWebModeration
{
    private readonly OsuWebDbContext _db;
    private readonly TimeProvider _time;

    public OsuWebModeration(OsuWebDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    public async Task<ModerationOutcome> RestrictAsync(int userId, int actorId, string reason, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == (uint)userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return ModerationOutcome.UnknownUser;
        if (OsuWebAccounts.IsRestricted(user.UserType, user.UserWarnings))
            return ModerationOutcome.AlreadyRestricted;

        user.UserWarnings = true;
        Add(userId, actorId, OsuWebAccountHistory.Restriction, reason, 0, permanent: true);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return ModerationOutcome.Done;
    }

    public async Task<ModerationOutcome> UnrestrictAsync(int userId, int actorId, string reason, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == (uint)userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return ModerationOutcome.UnknownUser;
        // A banned account type is changed from the admin panel, not here (as on the website).
        if (!user.UserWarnings)
            return ModerationOutcome.NotRestricted;

        user.UserWarnings = false;
        Add(userId, actorId, OsuWebAccountHistory.Note, $"Unrestricted: {reason}", 0, permanent: false);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return ModerationOutcome.Done;
    }

    public async Task<ModerationOutcome> SilenceAsync(int userId, int actorId, TimeSpan length, string reason, CancellationToken cancellationToken = default)
    {
        if (!await _db.Users.AnyAsync(u => u.UserId == (uint)userId, cancellationToken).ConfigureAwait(false))
            return ModerationOutcome.UnknownUser;

        Add(userId, actorId, OsuWebAccountHistory.Silence, reason, (uint)Math.Clamp(length.TotalSeconds, 1, uint.MaxValue), permanent: false);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return ModerationOutcome.Done;
    }

    public async Task<ModerationOutcome> UnsilenceAsync(int userId, int actorId, string reason, CancellationToken cancellationToken = default)
    {
        if (!await _db.Users.AnyAsync(u => u.UserId == (uint)userId, cancellationToken).ConfigureAwait(false))
            return ModerationOutcome.UnknownUser;
        if (await SilenceEndAsync(userId, cancellationToken).ConfigureAwait(false) is null)
            return ModerationOutcome.NotSilenced;

        Add(userId, actorId, OsuWebAccountHistory.Silence, $"Silence lifted: {reason}", 0, permanent: false);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return ModerationOutcome.Done;
    }

    /// <summary>
    /// Clears the player's account standing: deletes their history rows of the given kinds
    /// (<see cref="OsuWebAccountHistory.Note"/>, <see cref="OsuWebAccountHistory.Restriction"/>,
    /// <see cref="OsuWebAccountHistory.Silence"/>), so the profile shows nothing for them and an
    /// active silence ends. A restricted player keeps their restriction rows: lift it first.
    /// </summary>
    public async Task<(ModerationOutcome Outcome, int Removed)> ClearStandingAsync(int userId, IReadOnlyCollection<sbyte> kinds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == (uint)userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return (ModerationOutcome.UnknownUser, 0);
        if (kinds.Contains(OsuWebAccountHistory.Restriction) && OsuWebAccounts.IsRestricted(user.UserType, user.UserWarnings))
            return (ModerationOutcome.AlreadyRestricted, 0);

        var statuses = kinds.Select(k => (sbyte?)k).ToList();
        var removed = await _db.AccountHistory
            .Where(h => h.UserId == (uint)userId && statuses.Contains(h.BanStatus))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return (ModerationOutcome.Done, removed);
    }

    /// <summary>When the player's silence ends, or null when they aren't silenced (osu-web's rule).</summary>
    public async Task<DateTimeOffset?> SilenceEndAsync(int userId, CancellationToken cancellationToken = default)
    {
        var latest = await _db.AccountHistory.AsNoTracking()
            .Where(h => h.UserId == (uint)userId && (h.BanStatus == OsuWebAccountHistory.Restriction || h.BanStatus == OsuWebAccountHistory.Silence))
            .OrderByDescending(h => h.Timestamp).ThenByDescending(h => h.BanId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return latest is null ? null : OsuWebAccounts.SilenceEnd(latest.Timestamp, latest.Period, _time.GetUtcNow());
    }

    private void Add(int userId, int actorId, sbyte type, string reason, uint period, bool permanent) =>
        _db.AccountHistory.Add(new OsuWebAccountHistory
        {
            UserId = (uint)userId,
            BannerId = actorId > 0 ? (uint)actorId : null,
            BanStatus = type,
            Reason = reason.Length > 2000 ? reason[..2000] : reason,
            Period = period,
            Permanent = permanent,
            Timestamp = _time.GetUtcNow().UtcDateTime,
        });

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();
    }
}
