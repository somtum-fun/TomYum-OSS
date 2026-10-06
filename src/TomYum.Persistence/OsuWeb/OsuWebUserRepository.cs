using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// <see cref="IUserRepository"/> over osu-web's <c>phpbb_users</c> + group tables, so an account
/// created on the website logs in to the game and vice versa. Passwords need no conversion: both
/// sides store <c>bcrypt(md5_hex(password))</c>. Enabled by <see cref="DatabaseOptions.OsuWebName"/>.
/// </summary>
/// <remarks>
/// TomYum-only fields have no osu-web column and are not persisted here: <see cref="User.ApiKey"/>
/// reads back as <c>null</c>. Staff privileges map to osu-web groups per <see cref="OsuWebAccounts"/>.
/// </remarks>
public sealed class OsuWebUserRepository : IUserRepository
{
    private readonly OsuWebDbContext _db;

    public OsuWebUserRepository(OsuWebDbContext db) => _db = db;

    public async Task<User?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        if (id <= 0)
            return null;
        var userId = (uint)id;
        var row = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserId == userId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : await ToDomainAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<User?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(username);

        // Exact osu-web match first (keeps "a_b" and "a b" apart when both exist), then the
        // TomYum safe-name rules, which treat spaces and underscores alike.
        var clean = OsuWebAccounts.CleanUsername(username);
        var row = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UsernameClean == clean, cancellationToken).ConfigureAwait(false);
        if (row is not null)
            return await ToDomainAsync(row, cancellationToken).ConfigureAwait(false);

        return await GetBySafeNameAsync(User.MakeSafeName(username), cancellationToken).ConfigureAwait(false);
    }

    public async Task<User?> GetBySafeNameAsync(string safeName, CancellationToken cancellationToken = default)
    {
        var row = await FindBySafeNameAsync(safeName, cancellationToken).ConfigureAwait(false);
        return row is null ? null : await ToDomainAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SafeNameExistsAsync(string safeName, CancellationToken cancellationToken = default) =>
        await FindBySafeNameAsync(safeName, cancellationToken).ConfigureAwait(false) is not null;

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default) =>
        _db.Users.AsNoTracking().AnyAsync(u => u.UserEmail == email, cancellationToken);

    public async Task<User> AddAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var defaultGroup = await RequireGroupAsync(OsuWebAccounts.DefaultGroup, cancellationToken).ConfigureAwait(false);
        var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Mirrors osu-web's UserRegistration: empty text columns, default group as the display
        // group (with its colour/rank), and a membership row for it.
        var row = new OsuWebUser
        {
            UserId = user.Id > 0 ? (uint)user.Id : 0,
            GroupId = defaultGroup.GroupId,
            UserColour = defaultGroup.GroupColour,
            UserRank = defaultGroup.GroupRank,
            UserRegdate = user.CreationTime > 0 ? (uint)user.CreationTime : now,
            UserLastvisit = user.LatestActivity > 0 ? (uint)user.LatestActivity : now,
            UserPasschg = now,
            Username = user.Username,
            UsernameClean = OsuWebAccounts.CleanUsername(user.Username),
            UserPassword = user.PasswordBcrypt,
            UserEmail = string.IsNullOrEmpty(user.Email) ? null : user.Email,
            CountryAcronym = OsuWebAccounts.ToOsuWebCountry(user.Country),
            OsuSubscriber = user.Privileges.HasFlag(UserPrivileges.Supporter),
            // As osu-web's own sign-up does (the column is varchar(50)).
            UserIp = user.RegistrationIp is { Length: > 0 } ip ? ip[..Math.Min(ip.Length, 50)] : string.Empty,
        };

        var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            _db.Users.Add(row);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            _db.UserGroups.Add(new OsuWebUserGroup { GroupId = defaultGroup.GroupId, UserId = row.UserId });
            await SyncStaffGroupsAsync(row.UserId, user.Privileges, cancellationToken).ConfigureAwait(false);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        _db.ChangeTracker.Clear();

        return await GetByIdAsync((int)row.UserId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"osu-web user {row.UserId} vanished right after insert.");
    }

    public async Task<User> UpdateAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var userId = (uint)user.Id;
        var row = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"osu-web user {user.Id} does not exist.");

        if (row.UserPassword != user.PasswordBcrypt)
        {
            row.UserPassword = user.PasswordBcrypt;
            row.UserPasschg = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        row.Username = user.Username;
        row.UsernameClean = OsuWebAccounts.CleanUsername(user.Username);
        row.UserEmail = string.IsNullOrEmpty(user.Email) ? null : user.Email;
        row.CountryAcronym = OsuWebAccounts.ToOsuWebCountry(user.Country);
        row.UserLastvisit = (uint)Math.Max(0, user.LatestActivity);
        row.OsuSubscriber = user.Privileges.HasFlag(UserPrivileges.Supporter);

        var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await SyncStaffGroupsAsync(userId, user.Privileges, cancellationToken).ConfigureAwait(false);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        _db.ChangeTracker.Clear();

        return await GetByIdAsync(user.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"osu-web user {user.Id} vanished during update.");
    }

    public Task UpdateCountryAsync(int userId, string country, CancellationToken cancellationToken = default)
    {
        var id = (uint)userId;
        var acronym = OsuWebAccounts.ToOsuWebCountry(country);
        return _db.Users
            .Where(u => u.UserId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.CountryAcronym, acronym), cancellationToken);
    }

    /// <summary>
    /// Makes sure BanchoBot exists as a real osu-web account (so the website can show it and
    /// osu-web's <c>BANCHO_BOT_USER_ID</c> can point at it) in the <c>bot</c> group. The bot never
    /// logs in with a password; its hash is left empty so no password can match it.
    /// </summary>
    public async Task EnsureBotAsync(int botUserId, string botName, CancellationToken cancellationToken = default)
    {
        var id = (uint)botUserId;
        if (await _db.Users.AsNoTracking().AnyAsync(u => u.UserId == id, cancellationToken).ConfigureAwait(false))
            return;

        var botGroup = await RequireGroupAsync(OsuWebAccounts.BotGroup, cancellationToken).ConfigureAwait(false);
        var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            _db.Users.Add(new OsuWebUser
            {
                UserId = id,
                GroupId = botGroup.GroupId,
                UserColour = botGroup.GroupColour,
                UserRank = botGroup.GroupRank,
                UserRegdate = now,
                UserLastvisit = now,
                Username = botName,
                UsernameClean = OsuWebAccounts.CleanUsername(botName),
                CountryAcronym = "XX",
            });
            _db.UserGroups.Add(new OsuWebUserGroup { GroupId = botGroup.GroupId, UserId = id });
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        _db.ChangeTracker.Clear();
    }

    private async Task<OsuWebUser?> FindBySafeNameAsync(string safeName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(safeName);

        // Indexed lookups on username_clean first; the REPLACE scan only runs for names that mix
        // spaces and underscores, which the candidates can't express.
        foreach (var candidate in OsuWebAccounts.CleanNameCandidates(safeName))
        {
            var row = await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.UsernameClean == candidate, cancellationToken).ConfigureAwait(false);
            if (row is not null)
                return row;
        }

        return await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UsernameClean.Replace(" ", "_") == safeName, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<User> ToDomainAsync(OsuWebUser row, CancellationToken cancellationToken)
    {
        var groups = await (
                from membership in _db.UserGroups.AsNoTracking()
                join grp in _db.Groups.AsNoTracking() on membership.GroupId equals grp.GroupId
                where membership.UserId == row.UserId
                select grp.Identifier)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var restricted = OsuWebAccounts.IsRestricted(row.UserType, row.UserWarnings);

        return new User
        {
            IsRestricted = restricted,
            // Restricted players can't chat publicly anyway; a silence only matters otherwise.
            SilenceEndUtc = restricted ? null : await GetSilenceEndAsync(row.UserId, cancellationToken).ConfigureAwait(false),
            Id = (int)row.UserId,
            Username = row.Username,
            SafeName = User.MakeSafeName(row.Username),
            Email = row.UserEmail ?? string.Empty,
            PasswordBcrypt = row.UserPassword,
            Country = OsuWebAccounts.FromOsuWebCountry(row.CountryAcronym),
            CreationTime = row.UserRegdate,
            LatestActivity = row.UserLastvisit,
            Privileges = OsuWebAccounts.ToPrivileges(groups, row.OsuSubscriber),
            IsBot = groups.Contains(OsuWebAccounts.BotGroup),
            IsDeleted = OsuWebAccounts.IsDeleted(row.UserType),
        };
    }

    /// <summary>Ids of every restricted or banned account (hidden from leaderboards and rankings).</summary>
    public async Task<IReadOnlyList<int>> GetRestrictedIdsAsync(CancellationToken cancellationToken = default) =>
        await _db.Users.AsNoTracking()
            .Where(u => u.UserWarnings)
            .Select(u => (int)u.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// When the user's silence ends, as osu-web's <c>User::isSilenced</c> decides it: the newest
    /// restriction/silence entry, if it has a period that hasn't run out yet. Null when not silenced.
    /// </summary>
    private async Task<DateTimeOffset?> GetSilenceEndAsync(uint userId, CancellationToken cancellationToken)
    {
        var last = await _db.AccountHistory.AsNoTracking()
            .Where(h => h.UserId == userId && (h.BanStatus == OsuWebAccountHistory.Restriction || h.BanStatus == OsuWebAccountHistory.Silence))
            .OrderByDescending(h => h.Timestamp)
            .ThenByDescending(h => h.BanId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return OsuWebAccounts.SilenceEnd(last?.Timestamp, last?.Period ?? 0, DateTimeOffset.UtcNow);
    }

    /// <summary>Adds/removes TomYum-managed staff groups so they match <paramref name="privileges"/>.</summary>
    private async Task SyncStaffGroupsAsync(uint userId, UserPrivileges privileges, CancellationToken cancellationToken)
    {
        var wanted = OsuWebAccounts.StaffGroupsFor(privileges);
        var managed = OsuWebAccounts.StaffGroups.Select(s => s.Group).ToList();

        var groups = await _db.Groups.AsNoTracking()
            .Where(g => g.Identifier != null && managed.Contains(g.Identifier))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var groupIds = groups.Select(g => g.GroupId).ToList();
        var current = await _db.UserGroups
            .Where(m => m.UserId == userId && groupIds.Contains(m.GroupId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var identifier in wanted)
        {
            var group = groups.FirstOrDefault(g => g.Identifier == identifier)
                ?? throw MissingGroup(identifier);
            if (current.All(m => m.GroupId != group.GroupId))
                _db.UserGroups.Add(new OsuWebUserGroup { GroupId = group.GroupId, UserId = userId });
        }

        foreach (var membership in current)
        {
            var identifier = groups.First(g => g.GroupId == membership.GroupId).Identifier!;
            if (!wanted.Contains(identifier))
                _db.UserGroups.Remove(membership);
        }
    }

    private async Task<OsuWebGroup> RequireGroupAsync(string identifier, CancellationToken cancellationToken) =>
        await _db.Groups.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Identifier == identifier, cancellationToken).ConfigureAwait(false)
        ?? throw MissingGroup(identifier);

    private static InvalidOperationException MissingGroup(string identifier) => new(
        $"osu-web group '{identifier}' does not exist. osu-web's migrations don't create groups; run " +
        "`php artisan db:seed --class='Database\\Seeders\\ModelSeeders\\GroupSeeder'` in osu-web once " +
        "(see docs/osu-web.md).");
}
