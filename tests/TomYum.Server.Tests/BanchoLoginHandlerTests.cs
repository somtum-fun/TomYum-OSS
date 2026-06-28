using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// End-to-end auth tests through <see cref="BanchoLoginHandler"/>: a registered account can
/// log in with the md5 of its plaintext, a wrong password is rejected, and an unknown user is
/// rejected. Exercises registration → bcrypt storage → login verification together.
/// </summary>
public class BanchoLoginHandlerTests
{
    private static (BanchoLoginHandler Handler, PlayerRegistry Registry) NewHandler(out InMemoryUserRepository users)
    {
        users = new InMemoryUserRepository();
        var hasher = new BcryptPasswordHasher();
        var registry = new PlayerRegistry();
        var handler = new BanchoLoginHandler(users, hasher, registry, NullLogger<BanchoLoginHandler>.Instance);
        return (handler, registry);
    }

    private static async Task RegisterAsync(InMemoryUserRepository users, string username, string plaintext)
    {
        var registration = new RegistrationService(users, new BcryptPasswordHasher(), NullLogger<RegistrationService>.Instance);
        var result = await registration.RegisterAsync(
            username, $"{User_SafeEmail(username)}@tomyum.dev", plaintext, create: true, country: "xx", CancellationToken.None);
        Assert.True(result.IsValid);
    }

    private static string User_SafeEmail(string username) =>
        username.ToLowerInvariant().Replace(" ", string.Empty).Replace("_", string.Empty);

    private static string LoginBody(string username, string plaintext)
    {
        var hasher = new BcryptPasswordHasher();
        var pwMd5 = hasher.ComputeMd5Hex(plaintext);
        // username\npassword_md5\nversion|utc|city|hashes|pm\n
        return $"{username}\n{pwMd5}\nb20231228.1|0|0|abc:runningunderwine:def:ghi:jkl:|0\n";
    }

    [Fact]
    public async Task RegisteredUser_CorrectPassword_LogsIn()
    {
        var (handler, registry) = NewHandler(out var users);
        await RegisterAsync(users, "BlueSky", "s3cure-pass");

        var outcome = await handler.HandleAsync(LoginBody("BlueSky", "s3cure-pass"), CancellationToken.None);

        Assert.NotNull(outcome.Token);
        Assert.Equal(1, registry.Count);

        // The success burst opens with PROTOCOL_VERSION (id 75); a failure body would be just USER_ID.
        Assert.Equal((ushort)ServerPacketId.ProtocolVersion, FirstPacketId(outcome.Body));
    }

    /// <summary>Reads the id of the first framed packet. Kept synchronous because
    /// <see cref="BanchoPacketReader"/> is a ref struct and cannot live in an async method.</summary>
    private static ushort FirstPacketId(byte[] body)
    {
        var reader = new BanchoPacketReader(body);
        return (ushort)reader.ReadHeader().Id;
    }

    [Fact]
    public async Task RegisteredUser_WrongPassword_IsRejected()
    {
        var (handler, registry) = NewHandler(out var users);
        await RegisterAsync(users, "BlueSky", "s3cure-pass");

        var outcome = await handler.HandleAsync(LoginBody("BlueSky", "wrong-pass-xyz"), CancellationToken.None);

        Assert.Null(outcome.Token);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task UnknownUser_IsRejected()
    {
        var (handler, registry) = NewHandler(out _);

        var outcome = await handler.HandleAsync(LoginBody("NoSuchUser", "s3cure-pass"), CancellationToken.None);

        Assert.Null(outcome.Token);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task LoginByName_WithSpace_MatchesUnderscoreSafeName()
    {
        var (handler, _) = NewHandler(out var users);
        await RegisterAsync(users, "Blue_Sky", "s3cure-pass");

        // Logging in as "Blue Sky" (space) must resolve to the "blue_sky" safe name.
        var outcome = await handler.HandleAsync(LoginBody("Blue Sky", "s3cure-pass"), CancellationToken.None);

        Assert.NotNull(outcome.Token);
    }
}
