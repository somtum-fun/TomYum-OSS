using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Persistence.Repositories;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary><see cref="ClientHashRepository"/> against a real database (the upsert and matching are SQL).</summary>
public sealed class ClientHashRepositoryTests : OsuWebDatabaseTest
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string C = "cccccccccccccccccccccccccccccccc";

    [MySqlFact]
    public async Task Record_CountsRepeats_AndFindsOtherAccounts()
    {
        var repository = new ClientHashRepository(await NewTomYumContextAsync());
        var at = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        await repository.RecordAsync(Hash(1, A, B, C, at));
        await repository.RecordAsync(Hash(1, A, B, C, at.AddHours(1)));
        await repository.RecordAsync(Hash(2, A, A, A, at));
        await repository.RecordAsync(Hash(3, C, C, C, at.AddDays(1)));

        var own = Assert.Single(await repository.ListAsync(1));
        Assert.Equal(2, own.Occurrences);
        Assert.Equal(at.AddHours(1), own.LatestTime);

        Assert.Equal([2], await repository.FindOtherUsersAsync(1, new ClientHashQuery(A, null, null)));
        Assert.Equal([3, 1], await repository.FindOtherUsersAsync(2, new ClientHashQuery(B, null, C)));
        Assert.Empty(await repository.FindOtherUsersAsync(1, new ClientHashQuery(null, null, null)));
        Assert.Empty(await repository.FindOtherUsersAsync(1, new ClientHashQuery(null, B, null)));
    }

    [MySqlFact]
    public async Task Verify_IsIdempotent()
    {
        var repository = new ClientHashRepository(await NewTomYumContextAsync());

        Assert.False(await repository.IsVerifiedAsync(7));
        Assert.True(await repository.VerifyAsync(7, 0, DateTime.UtcNow));
        Assert.False(await repository.VerifyAsync(7, 3, DateTime.UtcNow));
        Assert.True(await repository.IsVerifiedAsync(7));
    }

    private static ClientHash Hash(int userId, string adapters, string uninstall, string disk, DateTime at) => new()
    {
        UserId = userId,
        OsuPath = A,
        Adapters = adapters,
        UninstallId = uninstall,
        DiskSerial = disk,
        LatestTime = at,
    };
}
