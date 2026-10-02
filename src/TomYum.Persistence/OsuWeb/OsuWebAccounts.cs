using TomYum.Domain.Enums;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// Translation rules between TomYum's account model and osu-web's. Kept free of I/O so the rules
/// are unit-testable; <see cref="OsuWebUserRepository"/> applies them.
/// </summary>
public static class OsuWebAccounts
{
    /// <summary>osu-web group every registered user belongs to.</summary>
    public const string DefaultGroup = "default";

    /// <summary>osu-web group for bot accounts (BanchoBot).</summary>
    public const string BotGroup = "bot";

    /// <summary><c>phpbb_users.user_type</c> of a banned account (osu-web <c>User::isBanned</c>).</summary>
    public const sbyte BannedUserType = 1;

    /// <summary>
    /// Restricted by staff (osu-web's <c>user_warnings</c>). A deleted account (the banned account type,
    /// the admin panel's Delete) isn't restricted: its scores stay on leaderboards, it just can't log in
    /// and isn't ranked.
    /// </summary>
    public static bool IsRestricted(sbyte userType, bool userWarnings) => userWarnings;

    /// <summary>Deleted from the admin panel (osu-web's banned account type): no logins, not ranked.</summary>
    public static bool IsDeleted(sbyte userType) => userType == BannedUserType;

    /// <summary>
    /// End of a silence that started at <paramref name="start"/> for <paramref name="periodSeconds"/>,
    /// or null when there's none, it was lifted (period 0) or it's over by <paramref name="now"/>.
    /// osu-web stores the start as a UTC timestamp.
    /// </summary>
    public static DateTimeOffset? SilenceEnd(DateTime? start, uint periodSeconds, DateTimeOffset now)
    {
        if (start is not { } started || periodSeconds == 0)
            return null;

        var end = new DateTimeOffset(DateTime.SpecifyKind(started, DateTimeKind.Utc)).AddSeconds(periodSeconds);
        return end > now ? end : null;
    }

    /// <summary>
    /// Staff privileges and the osu-web group each maps to. Supporter is not a group in osu-web
    /// (it is <c>phpbb_users.osu_subscriber</c>). Only these groups are ever added or removed by
    /// TomYum; any other osu-web membership (nat, bng, loved, …) is left alone.
    /// </summary>
    public static IReadOnlyList<(UserPrivileges Privilege, string Group)> StaffGroups { get; } =
    [
        (UserPrivileges.Administrator, "admin"),
        (UserPrivileges.Developer, "dev"),
        (UserPrivileges.Moderator, "gmt"),
    ];

    /// <summary>Privileges for an osu-web account from its group identifiers and supporter flag.</summary>
    public static UserPrivileges ToPrivileges(IEnumerable<string?> groupIdentifiers, bool osuSubscriber)
    {
        ArgumentNullException.ThrowIfNull(groupIdentifiers);

        var groups = groupIdentifiers.Where(g => g is not null).ToHashSet(StringComparer.Ordinal);
        var privileges = UserPrivileges.Normal;
        if (osuSubscriber)
            privileges |= UserPrivileges.Supporter;
        foreach (var (privilege, group) in StaffGroups)
        {
            if (groups.Contains(group))
                privileges |= privilege;
        }
        return privileges;
    }

    /// <summary>The staff groups <paramref name="privileges"/> should be a member of.</summary>
    public static IReadOnlySet<string> StaffGroupsFor(UserPrivileges privileges) =>
        StaffGroups.Where(s => privileges.HasFlag(s.Privilege)).Select(s => s.Group).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// osu-web's <c>username_clean</c>: PHP <c>strtolower</c>, which lower-cases ASCII only and
    /// keeps spaces (TomYum's safe name also turns spaces into underscores).
    /// </summary>
    public static string CleanUsername(string username)
    {
        ArgumentNullException.ThrowIfNull(username);
        return string.Create(username.Length, username, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;
            }
        });
    }

    /// <summary>
    /// <c>username_clean</c> values that are likely to match a TomYum safe name, cheapest first:
    /// the safe name itself (no spaces in the username) and all underscores read as spaces.
    /// Mixed names ("a_b c") need the repository's slower fallback.
    /// </summary>
    public static IReadOnlyList<string> CleanNameCandidates(string safeName)
    {
        ArgumentNullException.ThrowIfNull(safeName);
        var spaced = safeName.Replace('_', ' ');
        return spaced == safeName ? [safeName] : [safeName, spaced];
    }

    /// <summary>osu-web stores upper-case country codes (<c>XX</c> = unknown); TomYum uses lower-case.</summary>
    public static string ToOsuWebCountry(string country) =>
        string.IsNullOrWhiteSpace(country) ? "XX" : country.ToUpperInvariant();

    /// <summary>Inverse of <see cref="ToOsuWebCountry"/>.</summary>
    public static string FromOsuWebCountry(string countryAcronym) =>
        string.IsNullOrWhiteSpace(countryAcronym) ? "xx" : countryAcronym.ToLowerInvariant();
}
