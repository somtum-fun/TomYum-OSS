using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>Screenshot uploads: a player can't fill the server's disk with them.</summary>
public sealed class ScreenshotLimitTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tomyum-ss-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void ALimit_CountsPerPlayer_OverASlidingWindow()
    {
        var clock = new Clock();
        var limit = new PerUserLimit(2, TimeSpan.FromHours(1), clock);

        Assert.True(limit.TryTake(1));
        clock.Now += TimeSpan.FromMinutes(30);
        Assert.True(limit.TryTake(1));
        Assert.False(limit.TryTake(1));
        Assert.True(limit.TryTake(2)); // someone else

        // The first one is an hour old: room for one more.
        clock.Now += TimeSpan.FromMinutes(30);
        Assert.True(limit.TryTake(1));
        Assert.False(limit.TryTake(1));
    }

    [Fact]
    public async Task APlayer_GetsAFriendlyNo_AfterAnHoursWorthOfScreenshots()
    {
        var hasher = new BcryptPasswordHasher();
        var users = new InMemoryUserRepository();
        await users.AddAsync(new User
        {
            Username = "Snapper", SafeName = "snapper", Email = "s@x.dev", Country = "nl",
            PasswordBcrypt = hasher.HashPassword(hasher.ComputeMd5Hex("pw")),
        });
        using var services = new ServiceCollection()
            .AddSingleton<IUserRepository>(users)
            .AddSingleton<IPasswordHasher>(hasher)
            .BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Screenshots:Path"] = _directory }).Build();

        for (var i = 0; i < StableClientEndpoints.ScreenshotsPerHour; i++)
            Assert.IsType<ContentHttpResult>(await StableClientEndpoints.HandleScreenshotAsync(Upload(services, hasher), configuration, default));

        var refused = Assert.IsType<ContentHttpResult>(await StableClientEndpoints.HandleScreenshotAsync(Upload(services, hasher), configuration, default));
        Assert.Equal(StatusCodes.Status429TooManyRequests, refused.StatusCode);
        Assert.Contains("Please wait", refused.ResponseContent, StringComparison.Ordinal);
        Assert.Equal(StableClientEndpoints.ScreenshotsPerHour, Directory.GetFiles(_directory).Length);
    }

    private static DefaultHttpContext Upload(ServiceProvider services, BcryptPasswordHasher hasher)
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.ContentType = "multipart/form-data; boundary=x";
        context.Request.Form = new FormCollection(
            new Dictionary<string, StringValues> { ["u"] = "Snapper", ["p"] = hasher.ComputeMd5Hex("pw") },
            new FormFileCollection { new FormFile(new MemoryStream(png), 0, png.Length, "ss", "ss.png") });
        return context;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
