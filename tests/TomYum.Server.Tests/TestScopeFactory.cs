using Microsoft.Extensions.DependencyInjection;
using TomYum.Domain.Repositories;
using TomYum.Server.Bancho;

namespace TomYum.Server.Tests;

/// <summary>
/// Test helper that builds an <see cref="IServiceScopeFactory"/> backed by a single
/// <see cref="InMemoryUserRepository"/>, so <see cref="BanchoBotCommands"/> (which resolves the
/// scoped user repository per command) can be constructed in unit tests without a host.
/// </summary>
internal static class TestScopeFactory
{
    /// <summary>
    /// Returns a scope factory whose scopes resolve <paramref name="users"/> as the
    /// <see cref="IUserRepository"/>. A fresh in-memory repository is created when none is supplied.
    /// </summary>
    public static IServiceScopeFactory Create(IUserRepository? users = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(users ?? new InMemoryUserRepository());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}
