using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Domain.Enums;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// Tests the in-game registration validation + creation rules ported from bancho.py
/// (username/email/password rules, validate-only vs create, uniqueness, error keys).
/// See <c>memories/protocols/bancho-stable.md</c> §3 / the registration study note.
/// </summary>
public class RegistrationServiceTests
{
    private static RegistrationService NewService(out InMemoryUserRepository users)
    {
        users = new InMemoryUserRepository();
        return new RegistrationService(users, new BcryptPasswordHasher(), NullLogger<RegistrationService>.Instance);
    }

    [Fact]
    public async Task ValidInput_CreateFalse_ValidatesButDoesNotCreate()
    {
        var service = NewService(out var users);

        var result = await service.RegisterAsync(
            "BlueSky", "blue@sky.dev", "s3cure-pass", create: false, country: "th", CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Null(result.Created);
        Assert.Null(await users.GetByUsernameAsync("BlueSky", CancellationToken.None));
    }

    [Fact]
    public async Task ValidInput_CreateTrue_CreatesHashedAccount()
    {
        var service = NewService(out var users);

        var result = await service.RegisterAsync(
            "BlueSky", "blue@sky.dev", "s3cure-pass", create: true, country: "th", CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Created);
        Assert.Equal("bluesky", result.Created!.SafeName);
        Assert.Equal("th", result.Created.Country);
        Assert.StartsWith("$2", result.Created.PasswordBcrypt, StringComparison.Ordinal);

        // The account is persisted and looked up by safe name ("BlueSky" -> "bluesky").
        Assert.NotNull(await users.GetByUsernameAsync("BlueSky", CancellationToken.None));
    }

    [Theory]
    [InlineData("a")]                 // too short
    [InlineData("waaaaaaaaaaaaaaay")] // too long (>15)
    public async Task BadUsername_LengthError(string username)
    {
        var service = NewService(out _);

        var result = await service.RegisterAsync(
            username, "ok@email.dev", "s3cure-pass", create: true, country: "xx", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("username", result.Errors.Keys);
    }

    [Fact]
    public async Task Username_WithBothUnderscoreAndSpace_IsRejected()
    {
        var service = NewService(out _);

        var result = await service.RegisterAsync(
            "blue _sky", "ok@email.dev", "s3cure-pass", create: true, country: "xx", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("username", result.Errors.Keys);
    }

    [Fact]
    public async Task DuplicateUsername_IsRejected()
    {
        var service = NewService(out _);
        await service.RegisterAsync("BlueSky", "a@a.dev", "s3cure-pass", create: true, country: "xx", CancellationToken.None);

        // "bluesky" collides with "BlueSky" via the lowercase safe-name canonicalisation.
        var result = await service.RegisterAsync(
            "bluesky", "b@b.dev", "s3cure-pass", create: true, country: "xx", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("username", result.Errors.Keys);
    }

    [Fact]
    public async Task DuplicateEmail_IsRejected()
    {
        var service = NewService(out _);
        await service.RegisterAsync("FirstUser", "dup@email.dev", "s3cure-pass", create: true, country: "xx", CancellationToken.None);

        var result = await service.RegisterAsync(
            "SecondUser", "dup@email.dev", "s3cure-pass", create: true, country: "xx", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("user_email", result.Errors.Keys);
    }

    [Fact]
    public async Task BadEmail_SyntaxError()
    {
        var service = NewService(out _);

        var result = await service.RegisterAsync(
            "BlueSky", "not-an-email", "s3cure-pass", create: true, country: "xx", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("user_email", result.Errors.Keys);
    }

    [Theory]
    [InlineData("short")]   // < 8 chars
    [InlineData("aaaaaaaa")] // 8 chars but <= 3 unique
    public async Task BadPassword_IsRejected(string password)
    {
        var service = NewService(out _);

        var result = await service.RegisterAsync(
            "BlueSky", "ok@email.dev", password, create: true, country: "xx", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("password", result.Errors.Keys);
    }

    [Fact]
    public async Task FirstRegistration_IsGrantedOwnerPrivileges()
    {
        var service = NewService(out _);

        // A fresh in-memory repo assigns id 3 to the first registration (1 = BanchoBot,
        // 2 = dev seed), so the first real player becomes the server Owner automatically.
        var result = await service.RegisterAsync(
            "Owner", "owner@tomyum.dev", "s3cure-pass", create: true, country: "xx", CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(RegistrationService.OwnerUserId, result.Created!.Id);
        Assert.Equal(UserPrivileges.Owner, result.Created.Privileges);
        Assert.True(result.Created.Privileges.HasFlag(UserPrivileges.Administrator));
    }

    [Fact]
    public async Task FirstRegistration_WithOsuWebAccounts_IsNotAutoGrantedOwner()
    {
        // Ids are shared with website sign-ups there, and Owner would mean osu-web admin.
        var users = new InMemoryUserRepository();
        var service = new RegistrationService(
            users, new BcryptPasswordHasher(), NullLogger<RegistrationService>.Instance,
            new TomYum.Persistence.DatabaseOptions { Enabled = true, OsuWebName = "osu" });

        var result = await service.RegisterAsync(
            "Owner", "owner@tomyum.dev", "s3cure-pass", create: true, country: "xx", CancellationToken.None);

        Assert.Equal(RegistrationService.OwnerUserId, result.Created!.Id);
        Assert.Equal(UserPrivileges.Normal, result.Created.Privileges);
    }

    [Fact]
    public async Task SecondRegistration_IsOnlyNormal()
    {
        var service = NewService(out _);
        await service.RegisterAsync("Owner", "owner@tomyum.dev", "s3cure-pass", create: true, country: "xx", CancellationToken.None);

        var result = await service.RegisterAsync(
            "Player", "player@tomyum.dev", "s3cure-pass", create: true, country: "xx", CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.NotEqual(RegistrationService.OwnerUserId, result.Created!.Id);
        Assert.Equal(UserPrivileges.Normal, result.Created.Privileges);
        Assert.False(result.Created.Privileges.HasFlag(UserPrivileges.Administrator));
    }

    [Fact]
    public void PrivilegeMapper_Owner_MapsToStaffClientBitmask()
    {
        var bancho = PrivilegeMapper.ToBancho(UserPrivileges.Owner);

        // Owner = Normal + Supporter + Moderator + Administrator + Developer. The client bitmask
        // mirrors bancho.py's crossover: server Administrator -> client Developer, server Developer
        // -> client Owner (bit 3). Moderator is what lights the in-client staff badge.
        Assert.True(bancho.HasFlag(TomYum.Bancho.BanchoPrivileges.Player));
        Assert.True(bancho.HasFlag(TomYum.Bancho.BanchoPrivileges.Supporter));
        Assert.True(bancho.HasFlag(TomYum.Bancho.BanchoPrivileges.Moderator));
        Assert.True(bancho.HasFlag(TomYum.Bancho.BanchoPrivileges.Developer));
        Assert.True(bancho.HasFlag(TomYum.Bancho.BanchoPrivileges.Owner));
    }

    [Fact]
    public void PrivilegeMapper_Normal_IsPlayerOnly()
    {
        var bancho = PrivilegeMapper.ToBancho(UserPrivileges.Normal);

        Assert.Equal(TomYum.Bancho.BanchoPrivileges.Player, bancho);
    }
}
