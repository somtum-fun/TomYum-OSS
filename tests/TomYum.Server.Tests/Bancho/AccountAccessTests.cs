using Microsoft.Extensions.DependencyInjection;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests.Bancho;

/// <summary>Who may sign in once their password checks out (<see cref="AccountAccess"/>).</summary>
public sealed class AccountAccessTests
{
    [Fact]
    public async Task DeletedAndBotAccounts_CantSignIn_BotsCanOnIrc()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        Assert.True(await AccountAccess.CanAuthenticateAsync(new User { Id = 1, Username = "a" }, services, CancellationToken.None));
        Assert.False(await AccountAccess.CanAuthenticateAsync(new User { Id = 2, Username = "b", IsDeleted = true }, services, CancellationToken.None));
        Assert.False(await AccountAccess.CanAuthenticateAsync(new User { Id = 3, Username = "c", IsBot = true }, services, CancellationToken.None));
        Assert.True(await AccountAccess.CanAuthenticateAsync(new User { Id = 3, Username = "c", IsBot = true }, services, CancellationToken.None, allowBots: true));
        Assert.Equal(BanchoLoginHandler.DeletedNotice,
            await AccountAccess.RefusalAsync(new User { Id = 2, Username = "b", IsDeleted = true, IsBot = true }, services, allowBots: true, CancellationToken.None));
    }

    [Fact]
    public async Task BannedAccount_CantSignIn_ButItsClientReachesTheLogin()
    {
        var services = new ServiceCollection().AddSingleton<IAccountBans>(new BannedAccounts(4)).BuildServiceProvider();
        var banned = new User { Id = 4, Username = "d" };

        Assert.Equal(BanchoLoginHandler.BannedNotice, await AccountAccess.RefusalAsync(banned, services, allowBots: true, CancellationToken.None));
        Assert.False(await AccountAccess.CanAuthenticateAsync(banned, services, CancellationToken.None));
        // bancho_connect.php: the login's -3 is what tells them.
        Assert.True(await AccountAccess.CanAuthenticateAsync(banned, services, CancellationToken.None, allowBanned: true));
        Assert.True(await AccountAccess.CanAuthenticateAsync(new User { Id = 5, Username = "e" }, services, CancellationToken.None));
    }
}
