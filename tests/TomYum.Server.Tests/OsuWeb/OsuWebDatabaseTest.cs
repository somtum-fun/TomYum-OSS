using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>
/// Runs only when <c>TOMYUM_TEST_MYSQL</c> holds a MySQL connection string (no database name)
/// for a user that may create databases, e.g. <c>Server=127.0.0.1;User=root;Password=;</c>.
/// CI sets it; locally the tests are skipped without it.
/// </summary>
public sealed class MySqlFactAttribute : FactAttribute
{
    public const string EnvVar = "TOMYUM_TEST_MYSQL";

    public MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"Set {EnvVar} to a MySQL connection string to run osu-web integration tests.";
    }
}

/// <summary>Like <see cref="MySqlFactAttribute"/>, and also needs Redis in <c>TOMYUM_TEST_REDIS</c> (e.g. <c>127.0.0.1:6379</c>).</summary>
public sealed class MySqlAndRedisFactAttribute : FactAttribute
{
    public const string RedisEnvVar = "TOMYUM_TEST_REDIS";

    public MySqlAndRedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MySqlFactAttribute.EnvVar))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RedisEnvVar)))
        {
            Skip = $"Set {MySqlFactAttribute.EnvVar} and {RedisEnvVar} to run osu-web integration tests that use Redis.";
        }
    }
}

/// <summary>
/// Gives each test a fresh osu-web database built from <c>osu-web-schema.sql</c> (translated from
/// osu-web's migrations) and, for tests that need it, a fresh TomYum database (EnsureCreated).
/// Both are dropped afterwards.
/// </summary>
public abstract class OsuWebDatabaseTest : IAsyncLifetime
{
    private static readonly MySqlServerVersion ServerVersion = new(new Version(8, 0, 0));

    // Short: EF's migration lock is named after the database, and MySQL caps lock names at 64 characters.
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..16];
    private string? _server;

    /// <summary>Connection string for the osu-web database.</summary>
    protected string OsuWebConnectionString { get; private set; } = string.Empty;

    /// <summary>Connection string for the TomYum database (created lazily by <see cref="NewTomYumContextAsync"/>).</summary>
    protected string TomYumConnectionString { get; private set; } = string.Empty;

    private string OsuWebDatabase => $"tomyum_test_osuweb_{_suffix}";

    private string TomYumDatabase => $"tomyum_test_tomyum_{_suffix}";

    public async Task InitializeAsync()
    {
        _server = Environment.GetEnvironmentVariable(MySqlFactAttribute.EnvVar);
        if (string.IsNullOrWhiteSpace(_server))
            return;

        await AdminAsync($"CREATE DATABASE `{OsuWebDatabase}`");
        // Small pools: test classes run in parallel, each with its own databases (and pools).
        OsuWebConnectionString = new MySqlConnectionStringBuilder(_server) { Database = OsuWebDatabase, MaximumPoolSize = 4 }.ConnectionString;
        TomYumConnectionString = new MySqlConnectionStringBuilder(_server) { Database = TomYumDatabase, MaximumPoolSize = 4 }.ConnectionString;

        var ddl = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "OsuWeb", "osu-web-schema.sql"));
        await ExecuteAsync(ddl);
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_server))
            return;

        // Every test has its own databases, so its connection pools are never used again: close them,
        // or idle connections pile up across the run until MySQL refuses new ones.
        // EF's pools are keyed by the connection string Pomelo adds its own options to, so clearing
        // ours by name misses them: clear every pool (other tests just open fresh connections).
        await MySqlConnection.ClearAllPoolsAsync();

        await AdminAsync($"DROP DATABASE IF EXISTS `{OsuWebDatabase}`");
        await AdminAsync($"DROP DATABASE IF EXISTS `{TomYumDatabase}`");
    }

    protected OsuWebDbContext NewOsuWebContext() => new(
        new DbContextOptionsBuilder<OsuWebDbContext>().UseMySql(OsuWebConnectionString, ServerVersion).Options);

    /// <summary>A context over a TomYum database with its schema created by the migrations (as the server does).</summary>
    protected async Task<TomYumDbContext> NewTomYumContextAsync()
    {
        var context = NewTomYumContext();
        await context.MigrateAsync();
        return context;
    }

    /// <summary>A context over the TomYum database, schema untouched.</summary>
    protected TomYumDbContext NewTomYumContext() => new(
        new DbContextOptionsBuilder<TomYumDbContext>().UseMySql(TomYumConnectionString, ServerVersion).Options);

    /// <summary>Runs SQL against the osu-web database.</summary>
    protected async Task ExecuteAsync(string sql)
    {
        await using var connection = new MySqlConnection(OsuWebConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Reads one value from the osu-web database.</summary>
    protected async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new MySqlConnection(OsuWebConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull
            ? default!
            : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task AdminAsync(string sql)
    {
        await using var admin = new MySqlConnection(_server);
        await admin.OpenAsync();
        await using var command = new MySqlCommand(sql, admin);
        await command.ExecuteNonQueryAsync();
    }
}
