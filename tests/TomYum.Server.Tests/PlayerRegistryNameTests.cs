using TomYum.Bancho;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>Looking players up by name goes through an index of their own sessions.</summary>
public class PlayerRegistryNameTests
{
    [Fact]
    public void ByName_IgnoresCase_FollowsReLogins_AndSkipsTournamentWindows()
    {
        var registry = new PlayerRegistry();
        var first = registry.Add(new BanchoPresence { UserId = 7, Username = "Cookie" });
        registry.AddTournamentClient(new BanchoPresence { UserId = 7, Username = "Cookie" });

        Assert.Same(first, registry.GetByUsername("cOOKIE"));
        Assert.Equal(1, registry.Count);
        Assert.Single(registry.All);

        var again = registry.Add(new BanchoPresence { UserId = 7, Username = "Cookie" });
        Assert.Same(again, registry.GetByUsername("Cookie"));

        registry.Remove(first.Token); // the replaced session leaving doesn't take the name with it
        Assert.Same(again, registry.GetByUsername("Cookie"));

        registry.Remove(again.Token);
        Assert.Null(registry.GetByUsername("Cookie"));
        Assert.Equal(0, registry.Count);
        Assert.Null(registry.GetByUsername("Nobody"));
    }
}
