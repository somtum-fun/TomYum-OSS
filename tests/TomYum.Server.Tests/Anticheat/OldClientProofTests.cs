using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TomYum.Domain.Entities;
using TomYum.Server.Anticheat;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests.Anticheat;

/// <summary>Patched old builds sign their login and scores (Anticheat:OldClients).</summary>
public sealed class OldClientProofTests
{
    private const string Secret = "test-secret";

    /// <summary>What a patched build sends: its build, flags and the HMAC with its own key.</summary>
    private static string Proof(string build, OldClientFindings flags, string message)
    {
        var key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(build));
        var mac = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));
        return string.Create(CultureInfo.InvariantCulture, $"{build}:{(int)flags:x}:{mac}");
    }

    [Fact]
    public void AProof_SignsOnlyWhatItWasMadeFor()
    {
        var score = "md5:Player:x:300:0:0:0:0:0:1000:1:True:A:0:True:0:20140127:20140127";
        var proof = OldClientProof.Parse(Proof("b20140127", OldClientFindings.None, OldClientProof.ScoreMessage(score, OldClientFindings.None)));

        Assert.NotNull(proof);
        Assert.Equal("b20140127", proof.Build);
        Assert.True(proof.Signs(Secret, OldClientProof.ScoreMessage(score, OldClientFindings.None)));
        Assert.False(proof.Signs(Secret, OldClientProof.ScoreMessage(score.Replace("1000", "9999", StringComparison.Ordinal), OldClientFindings.None)));
        Assert.False(proof.Signs("another-secret", OldClientProof.ScoreMessage(score, OldClientFindings.None)));
        // Hiding a finding breaks the signature.
        Assert.False((proof with { Flags = OldClientFindings.CheatProcess }).Signs(Secret, OldClientProof.ScoreMessage(score, OldClientFindings.CheatProcess)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("b1:0")]
    [InlineData("b1:zz:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("b1:0:abc")]
    public void Garbage_IsNoProof(string? text) => Assert.Null(OldClientProof.Parse(text));

    [Fact]
    public void TheLoginLine_CarriesTheProof()
    {
        Assert.True(TomYum.Bancho.LoginRequest.TryParse("Player\nmd5\nb20140127|7|0|a:b:c:d:e:|0|tomyum-verify,tomyum-ac:b20140127:0:" + new string('a', 64) + "\n", out var request));
        Assert.Equal("tomyum-ac:b20140127:0:" + new string('a', 64), request!.AntiCheatProof);
        Assert.True(request.HandlesVerification);
    }

    [Fact]
    public async Task OldBuilds_LogInOnlyWithACleanProof()
    {
        var users = new InMemoryUserRepository();
        var hasher = new BcryptPasswordHasher();
        var md5 = hasher.ComputeMd5Hex("password");
        await users.AddAsync(new User { Username = "Player", SafeName = "player", Email = "p@x.dev", PasswordBcrypt = hasher.HashPassword(md5) });
        await users.AddAsync(new User { Username = "Today", SafeName = "today", Email = "t@x.dev", PasswordBcrypt = hasher.HashPassword(md5) });
        var login = new BanchoLoginHandler(users, hasher, new InMemoryUserStatsRepository(), new PlayerRegistry(), new ChannelRegistry(), NullLogger<BanchoLoginHandler>.Instance,
            oldClientProof: Options.Create(new OldClientProofOptions { Require = true, Secret = Secret }));
        string Body(string capabilities) => $"Player\n{md5}\nb20140127|7|0|a:b:c:d:e:|0{capabilities}\n";
        string Signed(OldClientFindings flags) => ",tomyum-ac:" + Proof("b20140127", flags, OldClientProof.LoginMessage("Player", md5, flags));

        Assert.Null((await login.HandleAsync(Body(""), "TH", CancellationToken.None)).Token);
        Assert.Null((await login.HandleAsync(Body("|tomyum-verify" + Signed(OldClientFindings.Debugger)), "TH", CancellationToken.None)).Token);
        Assert.NotNull((await login.HandleAsync(Body("|tomyum-verify" + Signed(OldClientFindings.None)), "TH", CancellationToken.None)).Token);
        // Today's stable isn't asked.
        // Without Require, it's only logged.
        var logging = new BanchoLoginHandler(users, hasher, new InMemoryUserStatsRepository(), new PlayerRegistry(), new ChannelRegistry(), NullLogger<BanchoLoginHandler>.Instance,
            oldClientProof: Options.Create(new OldClientProofOptions { Secret = Secret }));
        Assert.NotNull((await logging.HandleAsync(Body(""), "TH", CancellationToken.None)).Token);
        Assert.NotNull((await login.HandleAsync($"Today\n{md5}\nb20250101.1|7|0|a:b:c:d:e:|0\n", "TH", CancellationToken.None)).Token);
    }
}
