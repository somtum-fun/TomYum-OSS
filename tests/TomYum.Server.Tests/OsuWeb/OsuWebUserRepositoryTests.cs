using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;
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

/// <summary>
/// <see cref="OsuWebUserRepository"/> against osu-web's real account tables
/// (<c>osu-web-accounts.sql</c>, translated from osu-web's migrations). Each test gets a fresh database.
/// </summary>
public sealed class OsuWebUserRepositoryTests : IAsyncLifetime
{
    private readonly string _database = $"tomyum_osuweb_test_{Guid.NewGuid():N}";
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable(MySqlFactAttribute.EnvVar);
        if (string.IsNullOrWhiteSpace(server))
            return;

        await using (var admin = new MySqlConnection(server))
        {
            await admin.OpenAsync();
            await using var create = new MySqlCommand($"CREATE DATABASE `{_database}`", admin);
            await create.ExecuteNonQueryAsync();
        }

        _connectionString = new MySqlConnectionStringBuilder(server) { Database = _database }.ConnectionString;
        var ddl = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "OsuWeb", "osu-web-accounts.sql"));
        await ExecuteAsync(ddl);
    }

    public async Task DisposeAsync()
    {
        var server = Environment.GetEnvironmentVariable(MySqlFactAttribute.EnvVar);
        if (string.IsNullOrWhiteSpace(server))
            return;

        await using var admin = new MySqlConnection(server);
        await admin.OpenAsync();
        await using var drop = new MySqlCommand($"DROP DATABASE IF EXISTS `{_database}`", admin);
        await drop.ExecuteNonQueryAsync();
    }

    [MySqlFact]
    public async Task AddAsync_WritesAnAccountOsuWebAccepts()
    {
        var hasher = new BcryptPasswordHasher();
        var bcrypt = hasher.HashPassword(hasher.ComputeMd5Hex("game-password"));

        var created = await NewRepository().AddAsync(new User
        {
            Username = "Some Player",
            SafeName = User.MakeSafeName("Some Player"),
            Email = "player@example.com",
            PasswordBcrypt = bcrypt,
            Country = "th",
        });

        Assert.Equal(2, created.Id); // phpbb_users auto-increments from 2
        Assert.Equal("some_player", created.SafeName);
        Assert.Equal("th", created.Country);
        Assert.Equal(UserPrivileges.Normal, created.Privileges);

        // What osu-web will see.
        Assert.Equal("some player", await ScalarAsync<string>("SELECT username_clean FROM phpbb_users WHERE user_id = 2"));
        Assert.Equal("TH", await ScalarAsync<string>("SELECT country_acronym FROM phpbb_users WHERE user_id = 2"));
        Assert.Equal(bcrypt, await ScalarAsync<string>("SELECT user_password FROM phpbb_users WHERE user_id = 2"));
        Assert.Equal("default", await ScalarAsync<string>(
            "SELECT g.identifier FROM phpbb_users u JOIN phpbb_groups g ON g.group_id = u.group_id WHERE u.user_id = 2"));
        Assert.Equal("default", await ScalarAsync<string>(
            "SELECT g.identifier FROM phpbb_user_group m JOIN phpbb_groups g ON g.group_id = m.group_id " +
            "WHERE m.user_id = 2 AND m.user_pending = 0"));
    }

    [MySqlFact]
    public async Task WebsiteAccount_LogsInToTheGame()
    {
        // A row as osu-web's PasswordHelper::make writes it: PHP bcrypt ($2y$) of md5_hex(password).
        // Hash generated independently (OpenBSD bcrypt) for md5("website-password-123").
        const string phpHash = "$2y$10$9T0tbDe1jRkWY8oaSAMQrury.osEV5lMmhqxBCAtF1h9bFg79qCdO";
        await ExecuteAsync(
            "INSERT INTO phpbb_users (username, username_clean, user_password, user_email, country_acronym, " +
            "user_sig, user_occ, user_interests, group_id) " +
            $"VALUES ('Web User', 'web user', '{phpHash}', 'web@example.com', 'DE', '', '', '', 7)");

        var user = await NewRepository().GetByUsernameAsync("Web User");

        Assert.NotNull(user);
        Assert.Equal("de", user.Country);
        var hasher = new BcryptPasswordHasher();
        Assert.True(hasher.Verify(hasher.ComputeMd5Hex("website-password-123"), user.PasswordBcrypt));
        Assert.False(hasher.Verify(hasher.ComputeMd5Hex("wrong"), user.PasswordBcrypt));
    }

    [MySqlFact]
    public async Task SafeNameLookups_TreatSpacesAndUnderscoresAlike()
    {
        await ExecuteAsync(
            "INSERT INTO phpbb_users (username, username_clean, user_sig, user_occ, user_interests) VALUES " +
            "('Mixed_Name Here', 'mixed_name here', '', '', '')");
        var repository = NewRepository();

        Assert.NotNull(await repository.GetBySafeNameAsync("mixed_name_here"));
        Assert.True(await repository.SafeNameExistsAsync("mixed_name_here"));
        Assert.NotNull(await repository.GetByUsernameAsync("MIXED_NAME HERE"));
        Assert.Null(await repository.GetBySafeNameAsync("someone_else"));
    }

    [MySqlFact]
    public async Task UpdateAsync_SyncsStaffGroupsAndLeavesOthersAlone()
    {
        var repository = NewRepository();
        var user = await repository.AddAsync(NewUser("Staffer"));
        await ExecuteAsync($"INSERT INTO phpbb_user_group (group_id, user_id, user_pending) VALUES (11, {user.Id}, 0)"); // nat

        var owner = await repository.UpdateAsync(With(user, UserPrivileges.Owner));
        Assert.Equal(UserPrivileges.Owner, owner.Privileges);
        Assert.Equal(1, await ScalarAsync<int>($"SELECT osu_subscriber FROM phpbb_users WHERE user_id = {user.Id}"));

        var demoted = await repository.UpdateAsync(With(owner, UserPrivileges.Normal));
        Assert.Equal(UserPrivileges.Normal, demoted.Privileges);
        var remaining = await ScalarAsync<string>(
            "SELECT GROUP_CONCAT(g.identifier ORDER BY g.identifier) FROM phpbb_user_group m " +
            $"JOIN phpbb_groups g ON g.group_id = m.group_id WHERE m.user_id = {user.Id}");
        Assert.Equal("default,nat", remaining);
    }

    [MySqlFact]
    public async Task EnsureBotAsync_CreatesBanchoBotOnceWithoutTakingPlayerIds()
    {
        var repository = NewRepository();

        await repository.EnsureBotAsync(BanchoBot.UserId, BanchoBot.Name);
        await repository.EnsureBotAsync(BanchoBot.UserId, BanchoBot.Name);
        var player = await repository.AddAsync(NewUser("First Player"));

        Assert.Equal(1, await ScalarAsync<long>("SELECT COUNT(*) FROM phpbb_users WHERE user_id = 1"));
        Assert.Equal("bot", await ScalarAsync<string>(
            "SELECT g.identifier FROM phpbb_users u JOIN phpbb_groups g ON g.group_id = u.group_id WHERE u.user_id = 1"));
        Assert.Equal(string.Empty, await ScalarAsync<string>("SELECT user_password FROM phpbb_users WHERE user_id = 1"));
        Assert.Equal(2, player.Id);
    }

    [MySqlFact]
    public async Task UpdateCountryAsync_StoresOsuWebFormat()
    {
        var repository = NewRepository();
        var user = await repository.AddAsync(NewUser("Traveller"));

        await repository.UpdateCountryAsync(user.Id, "jp");

        Assert.Equal("JP", await ScalarAsync<string>($"SELECT country_acronym FROM phpbb_users WHERE user_id = {user.Id}"));
        Assert.Equal("jp", (await repository.GetByIdAsync(user.Id))!.Country);
    }

    [MySqlFact]
    public async Task AddAsync_WithoutSeededGroups_ExplainsTheFix()
    {
        await ExecuteAsync("DELETE FROM phpbb_groups");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => NewRepository().AddAsync(NewUser("Nobody")));

        Assert.Contains("GroupSeeder", error.Message, StringComparison.Ordinal);
    }

    private OsuWebUserRepository NewRepository()
    {
        var options = new DbContextOptionsBuilder<OsuWebDbContext>()
            .UseMySql(_connectionString, new MySqlServerVersion(new Version(8, 0, 0)))
            .Options;
        return new OsuWebUserRepository(new OsuWebDbContext(options));
    }

    private static User NewUser(string name) => new()
    {
        Username = name,
        SafeName = User.MakeSafeName(name),
        Email = $"{User.MakeSafeName(name)}@example.com",
        PasswordBcrypt = "$2a$10$abcdefghijklmnopqrstuuxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
        Country = "xx",
    };

    private static User With(User user, UserPrivileges privileges) => new()
    {
        Id = user.Id,
        Username = user.Username,
        SafeName = user.SafeName,
        Email = user.Email,
        PasswordBcrypt = user.PasswordBcrypt,
        Country = user.Country,
        CreationTime = user.CreationTime,
        LatestActivity = user.LatestActivity,
        Privileges = privileges,
    };

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
