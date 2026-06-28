using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// Round-trip tests for the osu!stable-compatible password hasher. The stored credential is
/// <c>bcrypt(md5_hex(plaintext))</c>; the client sends the md5 hex at login. See
/// <c>memories/protocols/bancho-stable.md</c> §3.
/// </summary>
public class BcryptPasswordHasherTests
{
    [Fact]
    public void ComputeMd5Hex_MatchesKnownVector()
    {
        var hasher = new BcryptPasswordHasher();

        // md5("password") = 5f4dcc3b5aa765d61d8327deb882cf99 (lowercase hex).
        Assert.Equal("5f4dcc3b5aa765d61d8327deb882cf99", hasher.ComputeMd5Hex("password"));
    }

    [Fact]
    public void HashPassword_ThenVerify_RoundTrips()
    {
        var hasher = new BcryptPasswordHasher();
        var md5 = hasher.ComputeMd5Hex("correct horse battery");

        var stored = hasher.HashPassword(md5);

        Assert.StartsWith("$2", stored, StringComparison.Ordinal);
        Assert.True(hasher.Verify(md5, stored));
    }

    [Fact]
    public void Verify_RejectsWrongPassword()
    {
        var hasher = new BcryptPasswordHasher();
        var stored = hasher.HashPassword(hasher.ComputeMd5Hex("the-right-one"));

        Assert.False(hasher.Verify(hasher.ComputeMd5Hex("a-wrong-one"), stored));
    }

    [Fact]
    public void Verify_CacheHit_StillMatchesAfterFirstCheck()
    {
        var hasher = new BcryptPasswordHasher();
        var md5 = hasher.ComputeMd5Hex("repeat-me-please");
        var stored = hasher.HashPassword(md5);

        // First call may populate the verification cache; the second must agree with it.
        Assert.True(hasher.Verify(md5, stored));
        Assert.True(hasher.Verify(md5, stored));
        Assert.False(hasher.Verify(hasher.ComputeMd5Hex("nope-nope-nope"), stored));
    }
}
