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
