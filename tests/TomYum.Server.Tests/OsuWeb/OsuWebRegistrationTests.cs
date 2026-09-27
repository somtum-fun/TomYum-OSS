using Microsoft.Extensions.DependencyInjection;
using TomYum.Domain.Repositories;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Which repositories the server gets with and without osu-web (no database needed).</summary>
public class OsuWebRegistrationTests
{
    [Fact]
    public void WithOsuWebName_AccountsAndBeatmapsGoThroughOsuWeb()
    {
        using var scope = Build(new DatabaseOptions { Enabled = true, OsuWebName = "osu" });

        Assert.IsType<OsuWebUserRepository>(scope.ServiceProvider.GetRequiredService<IUserRepository>());
        Assert.IsType<OsuWebMirroringBeatmapRepository>(scope.ServiceProvider.GetRequiredService<IBeatmapRepository>());
    }

    [Fact]
    public void WithoutOsuWebName_TomYumTablesOnly()
    {
        using var scope = Build(new DatabaseOptions { Enabled = true });

        Assert.IsType<UserRepository>(scope.ServiceProvider.GetRequiredService<IUserRepository>());
        Assert.IsType<BeatmapRepository>(scope.ServiceProvider.GetRequiredService<IBeatmapRepository>());
    }

    private static IServiceScope Build(DatabaseOptions options) =>
        new ServiceCollection()
            .AddTomYumPersistence(options)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true })
            .CreateScope();
}
