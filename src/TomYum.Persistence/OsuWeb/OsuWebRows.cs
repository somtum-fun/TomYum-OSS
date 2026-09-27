namespace TomYum.Persistence.OsuWeb;

// Row shapes for the osu-web tables TomYum reads and writes. Only the columns TomYum needs, plus
// the NOT NULL columns without a database default (an insert must supply them). Column names and
// types follow osu-web's migrations (base_tables + later alters); osu-web owns this schema, so
// TomYum never creates or alters these tables.

/// <summary>A row of osu-web's <c>phpbb_users</c>.</summary>
public sealed class OsuWebUser
{
    public uint UserId { get; set; }

    /// <summary>1 = banned (osu-web <c>User::isBanned</c>).</summary>
    public sbyte UserType { get; set; }

    /// <summary>The user's default (display) group.</summary>
    public uint GroupId { get; set; }

    public string? UserPermissions { get; set; }

    public string UserIp { get; set; } = string.Empty;

    /// <summary>Unix seconds.</summary>
    public uint UserRegdate { get; set; }

    public string Username { get; set; } = string.Empty;

    /// <summary><c>strtolower(username)</c> — spaces kept, unlike TomYum's safe name.</summary>
    public string UsernameClean { get; set; } = string.Empty;

    /// <summary><c>bcrypt(md5_hex(password))</c>, same as TomYum.</summary>
    public string UserPassword { get; set; } = string.Empty;

    /// <summary>Unix seconds of the last password change.</summary>
    public uint UserPasschg { get; set; }

    public string? UserEmail { get; set; }

    /// <summary>Unix seconds.</summary>
    public uint UserLastvisit { get; set; }

    /// <summary>Non-zero = restricted (osu-web <c>User::isRestricted</c>).</summary>
    public bool UserWarnings { get; set; }

    public string UserSig { get; set; } = string.Empty;

    public string UserOcc { get; set; } = string.Empty;

    public string UserInterests { get; set; } = string.Empty;

    public string UserColour { get; set; } = string.Empty;

    public uint UserRank { get; set; }

    public bool OsuSubscriber { get; set; }

    /// <summary>Upper-case ISO code (osu-web convention); TomYum uses lower-case.</summary>
    public string CountryAcronym { get; set; } = string.Empty;
}

/// <summary>A row of osu-web's <c>phpbb_groups</c> (only what TomYum needs).</summary>
public sealed class OsuWebGroup
{
    public uint GroupId { get; set; }

    /// <summary>Stable name such as <c>default</c>, <c>admin</c>, <c>bot</c> (osu-web <c>Group::PRIV_IDENTIFIERS</c>).</summary>
    public string? Identifier { get; set; }

    public string GroupColour { get; set; } = string.Empty;

    public uint GroupRank { get; set; }
}

/// <summary>A row of osu-web's <c>phpbb_user_group</c> (group membership).</summary>
public sealed class OsuWebUserGroup
{
    public uint GroupId { get; set; }

    public uint UserId { get; set; }

    public bool GroupLeader { get; set; }

    /// <summary>osu-web writes <c>0</c> when adding a member; it doesn't filter on it when reading.</summary>
    public bool UserPending { get; set; }
}
