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
        var channels = new ChannelRegistry();
        var handler = new BanchoLoginHandler(users, hasher, new InMemoryUserStatsRepository(), registry, channels, NullLogger<BanchoLoginHandler>.Instance);
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

    private static string LoginBody(string username, string plaintext, string version = "b20231228.1", string uninstall = "ghi")
    {
        var hasher = new BcryptPasswordHasher();
        var pwMd5 = hasher.ComputeMd5Hex(plaintext);
        // username\npassword_md5\nversion|utc|city|hashes|pm\n
        return $"{username}\n{pwMd5}\n{version}|0|0|abc:runningunderwine:def:{uninstall}:jkl:|0\n";
    }

    [Theory]
    [InlineData("b20140624", true)]   // an old build never asks to join: it's joined at login
    [InlineData("b20231228.1", false)] // today's client joins itself, after the auto-join list
    public async Task OldBuilds_AreJoinedToTheAutoJoinChannels(string version, bool joined)
    {
        var (handler, registry) = NewHandler(out var users);
        await RegisterAsync(users, "BlueSky", "s3cure-pass");

        var outcome = await handler.HandleAsync(LoginBody("BlueSky", "s3cure-pass", version), "TH", CancellationToken.None);

        Assert.NotNull(outcome.Token);
        var ids = PacketIds(outcome.Body);
        Assert.Contains((ushort)ServerPacketId.ChannelAutoJoin, ids);
        Assert.Equal(joined, ids.Contains((ushort)ServerPacketId.ChannelJoinSuccess));
        // Either way the server has them in the channels already.
        Assert.NotEmpty(registry.GetByToken(outcome.Token!)!.Channels);
    }

    private static List<ushort> PacketIds(byte[] body)
    {
        var ids = new List<ushort>();
        var offset = 0;
        while (offset + 7 <= body.Length)
        {
            ids.Add(BitConverter.ToUInt16(body, offset));
            offset += 7 + (int)BitConverter.ToUInt32(body, offset + 3);
        }
        return ids;
    }

    [Fact]
    public async Task RegisteredUser_CorrectPassword_LogsIn()
    {
        var (handler, registry) = NewHandler(out var users);
        await RegisterAsync(users, "BlueSky", "s3cure-pass");

        var outcome = await handler.HandleAsync(LoginBody("BlueSky", "s3cure-pass"), "TH", CancellationToken.None);

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

        var outcome = await handler.HandleAsync(LoginBody("BlueSky", "wrong-pass-xyz"), string.Empty, CancellationToken.None);

        Assert.Null(outcome.Token);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task UnknownUser_IsRejected()
    {
        var (handler, registry) = NewHandler(out _);

        var outcome = await handler.HandleAsync(LoginBody("NoSuchUser", "s3cure-pass"), string.Empty, CancellationToken.None);

        Assert.Null(outcome.Token);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task LoginByName_WithSpace_MatchesUnderscoreSafeName()
    {
        var (handler, _) = NewHandler(out var users);
        await RegisterAsync(users, "Blue_Sky", "s3cure-pass");

        // Logging in as "Blue Sky" (space) must resolve to the "blue_sky" safe name.
        var outcome = await handler.HandleAsync(LoginBody("Blue Sky", "s3cure-pass"), string.Empty, CancellationToken.None);

        Assert.NotNull(outcome.Token);
    }

    [Fact]
    public async Task SecondClient_WhileTheFirstIsActive_IsRefused()
    {
        var (handler, registry) = NewHandler(out var users);
        await RegisterAsync(users, "BlueSky", "s3cure-pass");
        var first = await handler.HandleAsync(LoginBody("BlueSky", "s3cure-pass"), "TH", CancellationToken.None);

        var second = await handler.HandleAsync(LoginBody("BlueSky", "s3cure-pass"), "TH", CancellationToken.None);

        Assert.Null(second.Token);
        Assert.Contains((ushort)ServerPacketId.Notification, PacketIds(second.Body));
        Assert.NotNull(registry.GetByToken(first.Token!)); // the first client stays online
    }

    [Fact]
    public async Task SecondClient_AfterTheFirstWentQuiet_ReplacesIt()
    {
        var (handler, registry) = NewHandler(out var users);
        await RegisterAsync(users, "BlueSky", "s3cure-pass");
        var first = await handler.HandleAsync(LoginBody("BlueSky", "s3cure-pass"), "TH", CancellationToken.None);
        registry.GetByToken(first.Token!)!.LastActivityUtc = DateTimeOffset.UtcNow - BanchoLoginHandler.ActiveSessionWindow - TimeSpan.FromSeconds(1);

        var second = await handler.HandleAsync(LoginBody("BlueSky", "s3cure-pass"), "TH", CancellationToken.None);

        Assert.NotNull(second.Token);
        Assert.Null(registry.GetByToken(first.Token!));
        Assert.Equal(second.Token, registry.GetByUserId(registry.GetByToken(second.Token!)!.UserId)!.Token);
    }

    [Fact]
    public async Task SecondClient_FromTheSameMachine_ReplacesIt()
    {
        // osu! closed or crashed without its LOGOUT getting out, and the player started it again.
        const string machine = "0123456789abcdef0123456789abcdef";
        var (handler, registry) = NewHandler(out var users);
        await RegisterAsync(users, "BlueSky", "s3cure-pass");
        var first = await handler.HandleAsync(LoginBody("BlueSky", "s3cure-pass", uninstall: machine), "TH", CancellationToken.None);

        var second = await handler.HandleAsync(LoginBody("BlueSky", "s3cure-pass", uninstall: machine), "TH", CancellationToken.None);
        var elsewhere = await handler.HandleAsync(LoginBody("BlueSky", "s3cure-pass", uninstall: "fedcba9876543210fedcba9876543210"), "TH", CancellationToken.None);

        Assert.NotNull(second.Token);
        Assert.Null(registry.GetByToken(first.Token!));
        Assert.Null(elsewhere.Token); // another machine still waits its turn
    }

    [Fact]
    public async Task SecondClient_AfterTheServerAskedForAReconnect_ReplacesIt()
    {
        var (handler, registry) = NewHandler(out var users);
        await RegisterAsync(users, "BlueSky", "s3cure-pass");
        var first = await handler.HandleAsync(LoginBody("BlueSky", "s3cure-pass"), "TH", CancellationToken.None);
        registry.GetByToken(first.Token!)!.RequestReconnect();

        var second = await handler.HandleAsync(LoginBody("BlueSky", "s3cure-pass"), "TH", CancellationToken.None);

        Assert.NotNull(second.Token);
        Assert.Null(registry.GetByToken(first.Token!));
    }

    [Fact]
    public async Task Welcome_IsTheDefaultWithoutSettings()
    {
        var (handler, _) = NewHandler(out var users);
        await RegisterAsync(users, "BlueSky", "s3cure-pass");

        var outcome = await handler.HandleAsync(LoginBody("BlueSky", "s3cure-pass"), "TH", CancellationToken.None);

        Assert.Contains("Welcome to TomYum, BlueSky!", System.Text.Encoding.UTF8.GetString(outcome.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TournamentClient_WhileThePlayerIsOnline_LogsIn()
    {
        var (handler, _) = NewHandler(out var users);
        await RegisterAsync(users, "BlueSky", "s3cure-pass");
        await handler.HandleAsync(LoginBody("BlueSky", "s3cure-pass"), "TH", CancellationToken.None);

        var window = await handler.HandleAsync(LoginBody("BlueSky", "s3cure-pass", "b20231228.1tourney"), "TH", CancellationToken.None);

        Assert.NotNull(window.Token);
    }

    [Fact]
    public async Task GameClient_WhileOnIrc_LogsIn()
    {
        var (handler, registry) = NewHandler(out var users);
        await RegisterAsync(users, "BlueSky", "s3cure-pass");
        var user = await users.GetBySafeNameAsync("bluesky", CancellationToken.None);
        registry.Add(new BanchoPresence { UserId = user!.Id, Username = "BlueSky" }).IsIrc = true;

        var game = await handler.HandleAsync(LoginBody("BlueSky", "s3cure-pass"), "TH", CancellationToken.None);

        Assert.NotNull(game.Token);
    }
}
