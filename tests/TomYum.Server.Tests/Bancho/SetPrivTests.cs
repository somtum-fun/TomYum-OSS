using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Bancho;

/// <summary>
/// <c>!setpriv</c>: judged by the caller's account as stored (not the login's privileges); nobody
/// changes their own role, and only roles below the caller's are given or taken.
/// </summary>
public class SetPrivTests
{
    private sealed class NoMaps : IOsuApiClient
    {
        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) => Task.FromResult<Beatmap?>(null);

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public bool SupportsSince => false;
    }

    private const UserPrivileges Admin = UserPrivileges.Normal | UserPrivileges.Supporter | UserPrivileges.Moderator | UserPrivileges.Administrator;

    private static async Task<(BanchoBotCommands Commands, InMemoryUserRepository Users, PlayerRegistry Players)> SetUpAsync()
    {
        var users = new InMemoryUserRepository();
        foreach (var (name, privileges) in new[] { ("Owner", UserPrivileges.Owner), ("Admin", Admin), ("Other Admin", Admin), ("Mod", UserPrivileges.Normal | UserPrivileges.Moderator), ("Player", UserPrivileges.Normal) })
            await users.AddAsync(new User { Username = name, SafeName = User.MakeSafeName(name), Email = name + "@x.dev", Privileges = privileges });
        var players = new PlayerRegistry();
        var commands = new BanchoBotCommands(new TomYumVersion(), players, new ChannelRegistry(), new MatchRegistry(), new BeatmapService(new NoMaps()),
            TestScopeFactory.Create(users), new Plugins.PluginCommandRegistry(), new Plugins.LoadedPluginRegistry());
        return (commands, users, players);
    }

    private static async Task<string?> RunAsync(BanchoBotCommands commands, InMemoryUserRepository users, PlayerRegistry players, string caller, string command, UserPrivileges? atLogin = null)
    {
        var account = (await users.GetBySafeNameAsync(User.MakeSafeName(caller)))!;
        var session = players.Add(new BanchoPresence { UserId = account.Id, Username = account.Username });
        return await commands.TryHandleAsync(session, command, PrivilegeMapper.ToBancho(atLogin ?? account.Privileges));
    }

    private static async Task<UserPrivileges> RoleOf(InMemoryUserRepository users, string name) =>
        (await users.GetBySafeNameAsync(User.MakeSafeName(name)))!.Privileges;

    private static async Task<List<UserPrivileges>> RolesAsync(InMemoryUserRepository users)
    {
        var roles = new List<UserPrivileges>();
        foreach (var name in new[] { "Owner", "Admin", "Other Admin", "Mod", "Player" })
            roles.Add(await RoleOf(users, name));
        return roles;
    }

    [Fact]
    public async Task AnAdmin_SetsRolesBelowTheirs()
    {
        var (commands, users, players) = await SetUpAsync();

        await RunAsync(commands, users, players, "Admin", "!setpriv Player mod");
        await RunAsync(commands, users, players, "Admin", "!setpriv Mod normal");

        Assert.Equal(UserPrivileges.Normal | UserPrivileges.Moderator, await RoleOf(users, "Player"));
        Assert.Equal(UserPrivileges.Normal, await RoleOf(users, "Mod"));
    }

    [Theory]
    [InlineData("!setpriv Admin owner", "You can't change your own role.")]
    [InlineData("!setpriv Player owner", "You can only give roles below your own.")]
    [InlineData("!setpriv Player admin", "You can only give roles below your own.")]
    [InlineData("!setpriv Owner normal", "Owner's role isn't below yours, so you can't change it.")]
    [InlineData("!setpriv other_admin normal", "Other Admin's role isn't below yours, so you can't change it.")]
    public async Task AnAdmin_CantRaiseThemselves_OrTouchTheirEquals(string command, string reply)
    {
        var (commands, users, players) = await SetUpAsync();
        var before = await RolesAsync(users);

        Assert.Equal(reply, await RunAsync(commands, users, players, "Admin", command));

        Assert.Equal(before, await RolesAsync(users));
    }

    [Fact]
    public async Task TheStoredRole_Counts_NotTheOneAtLogin()
    {
        // Demoted since they logged in: their session still says admin.
        var (commands, users, players) = await SetUpAsync();
        await RunAsync(commands, users, players, "Owner", "!setpriv Admin normal");

        var reply = await RunAsync(commands, users, players, "Admin", "!setpriv Player mod", atLogin: Admin);

        Assert.Equal("That command is staff-only.", reply);
        Assert.Equal(UserPrivileges.Normal, await RoleOf(users, "Player"));
    }
}
