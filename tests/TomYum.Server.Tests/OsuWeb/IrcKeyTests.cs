using Microsoft.Extensions.DependencyInjection;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Irc;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>With website accounts, IRC logins use the key from the website's account settings.</summary>
public sealed class IrcKeyTests : OsuWebDatabaseTest
{
    [MySqlFact]
    public async Task TheWebsitesKey_LogsIn_AndNothingElseDoes()
    {
        await ExecuteAsync("INSERT INTO osu_user_ircauth (user_id, token) VALUES (7, 'a1b2c3d4'), (8, NULL)");
        using var services = new ServiceCollection().AddScoped(_ => new OsuWebIrcKeys(NewOsuWebContext())).BuildServiceProvider();
        var tokens = new IrcTokens(website: services.GetRequiredService<IServiceScopeFactory>());

        Assert.True(tokens.FromWebsite);
        Assert.True(await tokens.VerifyAsync(7, "a1b2c3d4"));
        Assert.True(await tokens.VerifyAsync(7, " a1b2c3d4 "));
        Assert.False(await tokens.VerifyAsync(7, "a1b2c3d5"));
        Assert.False(await tokens.VerifyAsync(8, string.Empty));   // key deleted on the website
        Assert.False(await tokens.VerifyAsync(9, "a1b2c3d4"));     // never made one
    }
}
