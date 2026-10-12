namespace TomYum.Tools.ImportBanchoPy;

/// <summary>Which bancho.py the source database came from. Decides privilege bits and which optional tables exist.</summary>
internal enum Flavor
{
    /// <summary>osuAkatsuki/bancho.py.</summary>
    BanchoPy,

    /// <summary>osu-NoLimits/bancho.py-ex (bancho.py plus <c>users.irc_key</c>).</summary>
    BanchoPyEx,

    /// <summary>kawatapw/kawata.py (its own privilege bits, badges, wiped scores).</summary>
    Kawata,

    /// <summary>osu!somtum's private fork (nominations, ranking history, connections, NAT and helper bits).</summary>
    Somtum,
}

/// <summary>
/// The <c>users.priv</c> bits of one flavour. 0 means the flavour has no such bit. bancho.py's
/// are from <c>app/constants/privileges.py</c>; kawata.py's granular bits are folded into the
/// nearest osu-web group (a user who can manage beatmaps is a BN, who can ban is GMT, who can
/// change privileges is an admin).
/// </summary>
internal sealed record PrivilegeLayout(
    long Unrestricted, long Verified, long Whitelisted, long Supporter, long Premium, long Alumni,
    long TourneyManager, long Nominator, long Moderator, long Administrator, long Developer, long Nat, long Helper)
{
    public static readonly PrivilegeLayout BanchoPy = new(
        Unrestricted: 1, Verified: 2, Whitelisted: 4, Supporter: 16, Premium: 32, Alumni: 128,
        TourneyManager: 1024, Nominator: 2048, Moderator: 4096, Administrator: 8192, Developer: 16384, Nat: 0, Helper: 0);

    public static readonly PrivilegeLayout Somtum = BanchoPy with { Nat = 1 << 15, Helper = 1 << 17 };

    public static readonly PrivilegeLayout Kawata = new(
        Unrestricted: 1, Verified: 2, Supporter: 4, Whitelisted: 2L << 31, Premium: 2L << 32, Alumni: 2L << 33,
        TourneyManager: 2L << 20, Nominator: 2L << 7 /* ManageBeatmaps */, Moderator: 2L << 4 /* BanUsers */,
        Administrator: 2L << 15 /* ManagePrivs */, Developer: 2L << 34, Nat: 0, Helper: 0);

    public long Donor => Supporter | Premium;

    /// <summary>The osu-web groups a privilege bit stands for, in the order they are written.</summary>
    public IEnumerable<(long Bit, string Group, bool Rulesets)> Groups()
    {
        if (Nominator != 0) yield return (Nominator, "bng", true);
        if (Nat != 0) yield return (Nat, "nat", true);
        if (Moderator != 0) yield return (Moderator, "gmt", false);
        if (Administrator != 0) yield return (Administrator, "admin", false);
        if (Developer != 0) yield return (Developer, "dev", false);
        if (Alumni != 0) yield return (Alumni, "alumni", false);
        if (TourneyManager != 0) yield return (TourneyManager, "tc", false);
        if (Helper != 0) yield return (Helper, "spt", false);
    }
}

/// <summary>What the source database has, found by looking at it (information_schema).</summary>
internal sealed class SourceSchema
{
    private SourceSchema(string schema, Flavor flavor)
    {
        Schema = schema;
        Flavor = flavor;
        Privileges = flavor switch
        {
            Flavor.Kawata => PrivilegeLayout.Kawata,
            Flavor.Somtum => PrivilegeLayout.Somtum,
            _ => PrivilegeLayout.BanchoPy,
        };
    }

    public string Schema { get; }

    public Flavor Flavor { get; }

    public PrivilegeLayout Privileges { get; }

    /// <summary>Tables the core bancho.py schema has; a source missing one isn't a bancho.py database.</summary>
    public static readonly string[] CoreTables = ["users", "stats", "scores", "maps", "client_hashes", "relationships", "logs", "achievements", "user_achievements"];

    public bool HasOldName { get; private set; }

    public bool HasLocation { get; private set; }

    public bool HasUserpage { get; private set; }

    public bool HasUnlockedAt { get; private set; }

    public bool HasMapServer { get; private set; }

    public bool HasMapOwner { get; private set; }

    public bool HasMapsetUploader { get; private set; }

    public bool HasConnections { get; private set; }

    public bool HasRankingHistory { get; private set; }

    public bool HasNominations { get; private set; }

    public bool HasClans { get; private set; }

    public bool HasFavourites { get; private set; }

    public bool HasRatings { get; private set; }

    public bool HasRelationshipType { get; private set; }

    /// <summary>Looks at <paramref name="schema"/> and decides the flavour (unless <paramref name="forced"/>).</summary>
    public static async Task<SourceSchema> DetectAsync(Db db, string schema, Flavor? forced)
    {
        foreach (var table in CoreTables)
        {
            if (await db.TableExistsAsync(schema, table).ConfigureAwait(false) == 0)
                throw new InvalidOperationException($"{schema} has no `{table}` table: it isn't a bancho.py database (restore the dump into this MySQL server first).");
        }

        var flavor = forced ?? await GuessAsync(db, schema).ConfigureAwait(false);
        var s = new SourceSchema(schema, flavor)
        {
            HasOldName = await db.ColumnExistsAsync(schema, "users", "old_name").ConfigureAwait(false) > 0,
            HasLocation = await db.ColumnExistsAsync(schema, "users", "location").ConfigureAwait(false) > 0,
            HasUserpage = await db.ColumnExistsAsync(schema, "users", "userpage_content").ConfigureAwait(false) > 0,
            HasUnlockedAt = await db.ColumnExistsAsync(schema, "user_achievements", "unlocked_at").ConfigureAwait(false) > 0,
            HasMapServer = await db.ColumnExistsAsync(schema, "maps", "server").ConfigureAwait(false) > 0,
            HasMapOwner = await db.ColumnExistsAsync(schema, "maps", "owner_id").ConfigureAwait(false) > 0,
            HasMapsetUploader = await db.ColumnExistsAsync(schema, "mapsets", "uploaded_by").ConfigureAwait(false) > 0,
            HasConnections = await db.TableExistsAsync(schema, "user_connections").ConfigureAwait(false) > 0,
            HasRankingHistory = await db.TableExistsAsync(schema, "ranking_history").ConfigureAwait(false) > 0,
            HasNominations = await db.TableExistsAsync(schema, "beatmap_nominations").ConfigureAwait(false) > 0,
            HasClans = await db.TableExistsAsync(schema, "clans").ConfigureAwait(false) > 0,
            HasFavourites = await db.TableExistsAsync(schema, "favourites").ConfigureAwait(false) > 0,
            HasRatings = await db.TableExistsAsync(schema, "ratings").ConfigureAwait(false) > 0,
            HasRelationshipType = await db.ColumnExistsAsync(schema, "relationships", "type").ConfigureAwait(false) > 0,
        };
        return s;
    }

    private static async Task<Flavor> GuessAsync(Db db, string schema)
    {
        if (await db.TableExistsAsync(schema, "beatmap_nominations").ConfigureAwait(false) > 0
            || await db.TableExistsAsync(schema, "user_connections").ConfigureAwait(false) > 0)
            return Flavor.Somtum;
        if (await db.TableExistsAsync(schema, "privileges_groups").ConfigureAwait(false) > 0
            || await db.TableExistsAsync(schema, "users_ordr").ConfigureAwait(false) > 0
            || await db.TableExistsAsync(schema, "wiped_scores").ConfigureAwait(false) > 0)
            return Flavor.Kawata;
        if (await db.ColumnExistsAsync(schema, "users", "irc_key").ConfigureAwait(false) > 0)
            return Flavor.BanchoPyEx;
        return Flavor.BanchoPy;
    }

    public static Flavor ParseFlavor(string text) => text.ToUpperInvariant() switch
    {
        "BANCHOPY" or "BANCHO.PY" => Flavor.BanchoPy,
        "BANCHOPY-EX" or "BANCHO.PY-EX" or "EX" => Flavor.BanchoPyEx,
        "KAWATA" or "KAWATA.PY" => Flavor.Kawata,
        "SOMTUM" => Flavor.Somtum,
        _ => throw new ArgumentException($"Unknown flavour '{text}': banchopy, banchopy-ex, kawata or somtum.", nameof(text)),
    };
}
