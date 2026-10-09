using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using TomYum.Server.Admin;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>BanchoBot's moderation commands against real databases (<see cref="ModerationCommands"/>).</summary>
public sealed class ModerationCommandTests : OsuWebDatabaseTest
{
    [Theory]
    [InlineData("30s", 30)]
    [InlineData("10m", 600)]
    [InlineData("6h", 21600)]
    [InlineData("1D", 86400)]
    [InlineData("2w", 1209600)]
    [InlineData("90", 90)]
    public void Durations_Parse(string text, int seconds)
    {
        Assert.True(ModerationCommands.TryParseDuration(text, out var length));
        Assert.Equal(seconds, length.TotalSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0m")]
    [InlineData("-1h")]
    [InlineData("1y")]
    [InlineData("366d")]
    [InlineData("soon")]
    public void BadDurations_AreRejected(string text) => Assert.False(ModerationCommands.TryParseDuration(text, out _));

    [MySqlFact]
    public async Task Restrict_Unrestrict_Silence_Unsilence_WriteWhatTheWebsiteReads()
    {
        var (commands, users, _, gmt, player, _) = await SetUpAsync();

        Assert.Equal("Restricted Player One: botting.", await commands.RunAsync(gmt, "restrict", ["Player_One", "botting"]));
        Assert.True((await users.GetByIdAsync(player))!.IsRestricted);
        Assert.Equal("Player One is already restricted.", await commands.RunAsync(gmt, "ban", ["Player_One", "again"]));
        Assert.Equal("Unrestricted Player One: appeal accepted.", await commands.RunAsync(gmt, "unban", ["player_one", "appeal", "accepted"]));
        Assert.False((await users.GetByIdAsync(player))!.IsRestricted);

        Assert.StartsWith("Silenced Player One for 1 hour(s)", await commands.RunAsync(gmt, "silence", ["Player_One", "1h", "spam"]));
        var silenceEnd = (await users.GetByIdAsync(player))!.SilenceEndUtc;
        Assert.NotNull(silenceEnd);
        Assert.InRange((silenceEnd.Value - DateTimeOffset.UtcNow).TotalMinutes, 58, 61);

        Assert.StartsWith("Lifted Player One's silence", await commands.RunAsync(gmt, "unsilence", ["Player_One"]));
        Assert.Null((await users.GetByIdAsync(player))!.SilenceEndUtc);
        Assert.Equal("Player One isn't silenced.", await commands.RunAsync(gmt, "unsilence", ["Player_One"]));

        // The history the website shows, with who did it.
        Assert.Equal(4L, await ScalarAsync<long>($"SELECT COUNT(*) FROM osu_user_banhistory WHERE user_id = {player} AND banner_id = {gmt}"));
    }

    [MySqlFact]
    public async Task OnlyStaff_AndOnlyAdmins_OnOtherStaff()
    {
        var (commands, _, admin, gmt, player, _) = await SetUpAsync();

        Assert.Equal("That command is staff-only.", await commands.RunAsync(player, "restrict", ["Gmt_Person", "revenge"]));
        Assert.Equal("Only administrators can moderate other staff.", await commands.RunAsync(gmt, "silence", ["Admin_Person", "1h", "no"]));
        Assert.StartsWith("Silenced Gmt Person", await commands.RunAsync(admin, "silence", ["Gmt_Person", "10m", "test"]));
        Assert.StartsWith("Usage: !silence", await commands.RunAsync(gmt, "silence", ["Player_One", "1h"]));
        Assert.Equal("Length like 10m, 1h, 6h, 1d, 1w (up to 365d).", await commands.RunAsync(gmt, "silence", ["Player_One", "forever", "x"]));
        Assert.Equal("No such user \"Nobody\".", await commands.RunAsync(gmt, "restrict", ["Nobody", "x"]));
    }

    [MySqlFact]
    public async Task Alerts_ReachOnlinePlayers()
    {
        var (commands, _, _, gmt, player, players) = await SetUpAsync();
        var online = players.Add(new BanchoPresence { UserId = player, Username = "Player One" });

        Assert.Equal("Sent to Player One.", await commands.RunAsync(gmt, "alertu", ["Player_One", "hello", "there"]));
        Assert.Equal("Sent to 1 player(s).", await commands.RunAsync(gmt, "alert", ["server", "restart", "soon"]));

        var reader = new BanchoPacketReader(online.DequeueAll());
        var notices = new List<string>();
        while (reader.CanReadHeader)
        {
            var header = reader.ReadHeader();
            Assert.Equal((ushort)ServerPacketId.Notification, (ushort)header.Id);
            notices.Add(reader.ReadString());
        }
        Assert.Equal(["hello there", "server restart soon"], notices);
    }

    [MySqlFact]
    public async Task Alerts_AlsoReachLazerAndWebsitePlayers_AsAMessage()
    {
        var osuWeb = NewOsuWebContext();
        var users = new OsuWebUserRepository(osuWeb);
        var gmt = await users.AddAsync(NewUser("Gmt Person", UserPrivileges.Normal | UserPrivileges.Moderator));
        var stable = await users.AddAsync(NewUser("Stable Player", UserPrivileges.Normal));
        var lazer = await users.AddAsync(NewUser("Lazer Player", UserPrivileges.Normal));
        await users.AddAsync(NewUser("Offline Player", UserPrivileges.Normal));
        // New accounts count as just seen: only these two were active recently.
        await ExecuteAsync("UPDATE phpbb_users SET user_lastvisit = 0");
        await ExecuteAsync($"UPDATE phpbb_users SET user_lastvisit = UNIX_TIMESTAMP() WHERE user_id IN ({stable.Id}, {lazer.Id})");

        var players = new PlayerRegistry();
        players.Add(new BanchoPresence { UserId = stable.Id, Username = "Stable Player" });
        var services = new ServiceCollection()
            .AddSingleton<IUserRepository>(users)
            .AddScoped(_ => new OsuWebCommunity(NewOsuWebContext()))
            .AddSingleton(players)
            .AddSingleton<TomYum.Server.OsuWeb.OnlinePlayers>()
            .BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var website = new TomYum.Server.OsuWeb.OsuWebChatBridge(
            scopes, players, new ChannelRegistry(), new ChatLog(scopes, TimeProvider.System, NullLogger<ChatLog>.Instance),
            NullLogger<TomYum.Server.OsuWeb.OsuWebChatBridge>.Instance);
        var commands = new ModerationCommands(scopes, players, website: website);

        Assert.Equal("Sent to 1 player(s) in-game and 1 on lazer/the website.", await commands.RunAsync(gmt.Id, "alert", ["restart", "soon"]));
        Assert.Equal("Sent to Lazer Player as a message (they're not in stable).", await commands.RunAsync(gmt.Id, "alertu", ["Lazer_Player", "hi"]));
        Assert.Equal("Sent to Stable Player.", await commands.RunAsync(gmt.Id, "alertu", ["Stable_Player", "hi"]));
    }

    private async Task<(ModerationCommands Commands, IUserRepository Users, int Admin, int Gmt, int Player, PlayerRegistry Players)> SetUpAsync()
    {
        var tomyum = await NewTomYumContextAsync();
        var osuWeb = NewOsuWebContext();
        var users = new OsuWebUserRepository(osuWeb);
        var admin = await users.AddAsync(NewUser("Admin Person", UserPrivileges.Normal | UserPrivileges.Moderator | UserPrivileges.Administrator));
        var gmt = await users.AddAsync(NewUser("Gmt Person", UserPrivileges.Normal | UserPrivileges.Moderator));
        var player = await users.AddAsync(NewUser("Player One", UserPrivileges.Normal));

        var players = new PlayerRegistry();
        var services = new ServiceCollection()
            .AddSingleton<IUserRepository>(users)
            .AddSingleton(new OsuWebModeration(osuWeb))
            .AddSingleton<IStaffLogRepository>(new StaffLogRepository(tomyum))
            .AddSingleton(new UserModerationSync(users, players, new RestrictedUsers(), TimeProvider.System, NullLogger<UserModerationSync>.Instance))
            .BuildServiceProvider();
        var commands = new ModerationCommands(services.GetRequiredService<IServiceScopeFactory>(), players);
        return (commands, users, admin.Id, gmt.Id, player.Id, players);
    }

    private static User NewUser(string name, UserPrivileges privileges) => new()
    {
        Username = name,
        SafeName = User.MakeSafeName(name),
        Email = $"{User.MakeSafeName(name)}@example.com",
        PasswordBcrypt = "x",
        Country = "nl",
        Privileges = privileges,
    };
}
