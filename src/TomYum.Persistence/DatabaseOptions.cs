using System.Globalization;

namespace TomYum.Persistence;

/// <summary>
/// MySQL connection settings, bound from the <c>Database</c> configuration section. The discrete
/// fields (host/port/name/user) live in <c>config/tomyum.yaml</c> (copy from
/// <c>tomyum.yaml.example</c>) or <c>appsettings.json</c>; the
/// password is injected via <c>TOMYUM_DATABASE__PASSWORD</c> and never committed (ADR-0008).
/// </summary>
/// <remarks>
/// A pre-built <see cref="ConnectionString"/> may be supplied directly (it wins when set); otherwise
/// <see cref="BuildConnectionString"/> composes one from the discrete fields. This keeps the common
/// case (separate fields + env password) simple while still allowing a full override.
/// </remarks>
public sealed class DatabaseOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Database";

    /// <summary>
    /// Whether MySQL persistence is active. When <c>false</c> the server falls back to the
    /// in-memory repository (tests and quick local runs without a database). Turn on in
    /// <c>config</c>/env once a database is reachable.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Database host. Defaults to <c>localhost</c> for local development.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Database port.</summary>
    public int Port { get; set; } = 3306;

    /// <summary>Schema/database name.</summary>
    public string Name { get; set; } = "tomyum";

    /// <summary>Database user.</summary>
    public string User { get; set; } = "tomyum";

    /// <summary>Database password (injected from the environment, never committed).</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Optional full connection string. When set (non-empty), it is used verbatim and the discrete
    /// fields are ignored — useful for environments that supply a single connection URI.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Name of osu-web's database on the same MySQL server (typically <c>osu</c>). When set, player
    /// accounts live in osu-web's <c>phpbb_users</c> instead of TomYum's own <c>users</c> table, so
    /// one account works on the website and in-game. Empty keeps TomYum's own table. See
    /// <c>docs/osu-web.md</c>.
    /// </summary>
    public string? OsuWebName { get; set; }

    /// <summary>
    /// osu-web's Elasticsearch index prefix (its <c>ES_INDEX_PREFIX</c>, empty by default). New
    /// scores are queued for osu-web's score indexer under this prefix.
    /// </summary>
    public string OsuWebIndexPrefix { get; set; } = string.Empty;

    /// <summary>osu-web's chat database (its <c>DB_DATABASE_CHAT</c>), where private messages are.</summary>
    public string OsuWebChatName { get; set; } = "osu_chat";

    /// <summary>osu-web's multiplayer database (its <c>DB_DATABASE_MP</c>), where match history is.</summary>
    public string OsuWebMpName { get; set; } = "osu_mp";

    /// <summary>
    /// Whether the osu! API's beatmap pack tags create packs on the website (<c>osu_beatmappacks</c>)
    /// and set each imported set's pack membership. Off by default: staff make this server's packs
    /// on the website (Staff panel › Beatmap packs). <c>TOMYUM_DATABASE__OSUWEBIMPORTPACKS=true</c>
    /// brings back osu!'s packs.
    /// </summary>
    public bool OsuWebImportPacks { get; set; }

    /// <summary>Whether accounts are stored in osu-web's database (database enabled and <see cref="OsuWebName"/> set).</summary>
    public bool UseOsuWebAccounts => Enabled && !string.IsNullOrWhiteSpace(OsuWebName);

    /// <summary>
    /// The effective connection string pointed at osu-web's database: same server and credentials
    /// as <see cref="BuildConnectionString"/>, with the database swapped for <see cref="OsuWebName"/>.
    /// </summary>
    public string BuildOsuWebConnectionString() =>
        new MySqlConnector.MySqlConnectionStringBuilder(BuildConnectionString())
        {
            Database = OsuWebName,
        }.ConnectionString;

    /// <summary>
    /// Returns the effective connection string: <see cref="ConnectionString"/> when supplied,
    /// otherwise one composed from the discrete fields.
    /// </summary>
    public string BuildConnectionString() =>
        string.IsNullOrWhiteSpace(ConnectionString)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Server={Host};Port={Port};Database={Name};User={User};Password={Password};")
            : ConnectionString;
}
