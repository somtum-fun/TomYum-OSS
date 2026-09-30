using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>
/// <see cref="OsuWebUserRepository"/> against osu-web's real account tables. Each test gets a fresh database.
/// </summary>
public sealed class OsuWebUserRepositoryTests : OsuWebDatabaseTest
{
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

        Assert.Equal(4, created.Id); // ids up to BanchoBot's (3) are reserved
        Assert.Equal("some_player", created.SafeName);
        Assert.Equal("th", created.Country);
        Assert.Equal(UserPrivileges.Normal, created.Privileges);

        // What osu-web will see.
        Assert.Equal("some player", await ScalarAsync<string>("SELECT username_clean FROM phpbb_users WHERE user_id = 4"));
        Assert.Equal("TH", await ScalarAsync<string>("SELECT country_acronym FROM phpbb_users WHERE user_id = 4"));
        Assert.Equal(bcrypt, await ScalarAsync<string>("SELECT user_password FROM phpbb_users WHERE user_id = 4"));
        Assert.Equal("default", await ScalarAsync<string>(
            "SELECT g.identifier FROM phpbb_users u JOIN phpbb_groups g ON g.group_id = u.group_id WHERE u.user_id = 4"));
        Assert.Equal("default", await ScalarAsync<string>(
            "SELECT g.identifier FROM phpbb_user_group m JOIN phpbb_groups g ON g.group_id = m.group_id " +
            "WHERE m.user_id = 4 AND m.user_pending = 0"));
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

        Assert.Equal(1, await ScalarAsync<long>($"SELECT COUNT(*) FROM phpbb_users WHERE user_id = {BanchoBot.UserId}"));
        Assert.Equal("bot", await ScalarAsync<string>(
            $"SELECT g.identifier FROM phpbb_users u JOIN phpbb_groups g ON g.group_id = u.group_id WHERE u.user_id = {BanchoBot.UserId}"));
        Assert.Equal(string.Empty, await ScalarAsync<string>($"SELECT user_password FROM phpbb_users WHERE user_id = {BanchoBot.UserId}"));
        Assert.Equal(BanchoBot.UserId + 1, player.Id);
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

    private OsuWebUserRepository NewRepository() => new(NewOsuWebContext());

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
}
