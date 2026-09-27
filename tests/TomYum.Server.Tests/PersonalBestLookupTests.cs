using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// The leaderboard's "personal best" needs the requester: osu!stable sends its username as <c>us</c>
/// (it used to be looked up as a session token, so the personal best was always empty).
/// </summary>
public sealed class PersonalBestLookupTests
{
    [Fact]
    public async Task OnlinePlayer_IsFoundByName_CaseInsensitively()
    {
        var registry = new PlayerRegistry();
        registry.Add(new BanchoPresence { UserId = 2, Username = "Lilian" });

        Assert.Equal(2, await WebEndpoints.ResolveRequesterIdAsync("lilian", registry, new InMemoryUserRepository(), CancellationToken.None));
    }

    [Fact]
    public async Task OfflinePlayer_IsFoundByAccount_IncludingSpacesInTheName()
    {
        var users = new InMemoryUserRepository();
        var user = await users.AddAsync(new User { Username = "Blue Sky", Email = "b@tomyum.dev" });

        Assert.Equal(user.Id, await WebEndpoints.ResolveRequesterIdAsync("Blue Sky", new PlayerRegistry(), users, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nobody")]
    public async Task UnknownOrMissing_IsNull(string username)
    {
        Assert.Null(await WebEndpoints.ResolveRequesterIdAsync(username, new PlayerRegistry(), new InMemoryUserRepository(), CancellationToken.None));
    }
}
