using TomYum.Bancho;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary><see cref="PlayerRegistry.SpectatingSessions"/>: a user's game session and tournament windows watching a host.</summary>
public class SpectatingSessionsTests
{
    [Fact]
    public void FindsTheGameAndTournamentWindows_WatchingThatHost_Only()
    {
        var registry = new PlayerRegistry();
        var main = registry.Add(new BanchoPresence { UserId = 7, Username = "Ref" });
        var window1 = registry.AddTournamentClient(new BanchoPresence { UserId = 7, Username = "Ref" })!;
        var window2 = registry.AddTournamentClient(new BanchoPresence { UserId = 7, Username = "Ref" })!;
        main.SpectatingUserId = 1;
        window1.SpectatingUserId = 1;
        window2.SpectatingUserId = 2;

        Assert.Equal([main, window1], registry.SpectatingSessions(7, 1));
        Assert.Equal([window2], registry.SpectatingSessions(7, 2));
        Assert.Empty(registry.SpectatingSessions(8, 1));

        registry.Remove(window1.Token);
        Assert.Equal([main], registry.SpectatingSessions(7, 1));
        registry.Remove(main.Token);
        Assert.Empty(registry.SpectatingSessions(7, 1));
        Assert.Equal([window2], registry.SpectatingSessions(7, 2));
    }

    [Fact]
    public void TournamentWindows_StillCappedPerAccount_AndFreedOnLeaving()
    {
        var registry = new PlayerRegistry();
        var windows = Enumerable.Range(0, PlayerRegistry.MaxTournamentClients)
            .Select(_ => registry.AddTournamentClient(new BanchoPresence { UserId = 9, Username = "T" })).ToList();
        Assert.All(windows, Assert.NotNull);
        Assert.Null(registry.AddTournamentClient(new BanchoPresence { UserId = 9, Username = "T" }));

        registry.Remove(windows[0]!.Token);
        Assert.NotNull(registry.AddTournamentClient(new BanchoPresence { UserId = 9, Username = "T" }));
    }
}
