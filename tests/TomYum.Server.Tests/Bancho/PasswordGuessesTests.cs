using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using TomYum.Domain.Entities;
using TomYum.Domain.Services;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests.Bancho;

/// <summary>The wrong-password limit every credential check goes through.</summary>
public sealed class PasswordGuessesTests
{
    private const string Right = "right";

    [Fact]
    public async Task TooManyWrongGuesses_RefuseEvenTheRightPassword_UntilTheWindowEnds()
    {
        var clock = new ManualClock();
        var (hasher, inner) = Guarded(clock);
        for (var i = 0; i < PasswordGuesses.AccountLimit; i++)
            Assert.False(await hasher.VerifyAsync("guess" + i, "stored-a"));

        Assert.False(await hasher.VerifyAsync(Right, "stored-a"));
        Assert.Equal(PasswordGuesses.AccountLimit, inner.Checks);   // refused without bcrypt
        Assert.True(await hasher.VerifyAsync(Right, "stored-b"));              // other accounts are fine

        clock.Advance(PasswordGuesses.Window + TimeSpan.FromSeconds(1));
        Assert.True(await hasher.VerifyAsync(Right, "stored-a"));
    }

    [Fact]
    public async Task TheSameWrongPassword_CountsOnce_AndSkipsBcrypt()
    {
        var (hasher, inner) = Guarded(new ManualClock());
        for (var i = 0; i < 50; i++)
            Assert.False(await hasher.VerifyAsync("old password", "stored"));

        Assert.Equal(1, inner.Checks);
        Assert.True(await hasher.VerifyAsync(Right, "stored"));   // a client stuck on an old password locks no one out
    }

    [Fact]
    public async Task TheRightPassword_ClearsTheAccountsCount()
    {
        var (hasher, _) = Guarded(new ManualClock());
        for (var i = 0; i < PasswordGuesses.AccountLimit - 1; i++)
            Assert.False(await hasher.VerifyAsync("guess" + i, "stored"));
        Assert.True(await hasher.VerifyAsync(Right, "stored"));
        for (var i = 0; i < PasswordGuesses.AccountLimit - 1; i++)
            Assert.False(await hasher.VerifyAsync("other" + i, "stored"));

        Assert.True(await hasher.VerifyAsync(Right, "stored"));
    }

    [Fact]
    public async Task OneAddress_GuessingAcrossAccounts_IsRefused()
    {
        var (hasher, _) = Guarded(new ManualClock());
        using (PasswordGuesses.From("203.0.113.9"))
        {
            for (var i = 0; i < PasswordGuesses.AddressLimit; i++)
                Assert.False(await hasher.VerifyAsync("guess", "stored-" + i));
            Assert.False(await hasher.VerifyAsync(Right, "someone-else"));
        }

        Assert.True(await hasher.VerifyAsync(Right, "someone-else"));                // from elsewhere
        using (PasswordGuesses.From("2001:db8::1"))
            Assert.True(await hasher.VerifyAsync(Right, "someone-else"));
    }

    [Fact]
    public async Task GuessesFromElsewhere_DontLockThePlayerOut()
    {
        // Guessing a player's password from another address (osu-rate.php?u=<name>&p=<anything>, ...)
        // mustn't refuse their own logins or score submissions.
        var (hasher, _) = Guarded(new ManualClock());
        using (PasswordGuesses.From("198.51.100.7"))
        {
            for (var i = 0; i < PasswordGuesses.AccountLimit; i++)
                Assert.False(await hasher.VerifyAsync("guess" + i, "stored"));
            Assert.False(await hasher.VerifyAsync(Right, "stored"));   // the guesser's own address is refused
        }

        using (PasswordGuesses.From("203.0.113.20"))
            Assert.True(await hasher.VerifyAsync(Right, "stored"));
    }

    [Fact]
    public async Task GuessesSpreadOverAddresses_CostAnAddressEveryFew_ButTyposDont()
    {
        var (hasher, _) = Guarded(new ManualClock());
        var n = 0;
        for (var address = 1; n < PasswordGuesses.AccountLimit; address++)
        {
            using (PasswordGuesses.From($"198.51.100.{address}"))
            {
                for (var i = 0; i < PasswordGuesses.SuspectGuesses && n < PasswordGuesses.AccountLimit; i++)
                    Assert.False(await hasher.VerifyAsync("guess" + n++, "stored"));
            }
        }

        // The account is guarded now: an address that guessed SuspectGuesses times is refused even with
        // the right password; one that mistyped once or twice still gets in.
        using (PasswordGuesses.From("198.51.100.1"))
            Assert.False(await hasher.VerifyAsync(Right, "stored"));
        using (PasswordGuesses.From("203.0.113.30"))
        {
            Assert.False(await hasher.VerifyAsync("typo", "stored"));
            Assert.False(await hasher.VerifyAsync("typo2", "stored"));
            Assert.True(await hasher.VerifyAsync(Right, "stored"));
        }
    }

    [Fact]
    public void IPv6_IsCountedByItsSlash64()
    {
        Assert.Equal(PasswordGuesses.AddressKey("2001:db8:1:2::1"), PasswordGuesses.AddressKey("2001:db8:1:2:ffff::9"));
        Assert.NotEqual(PasswordGuesses.AddressKey("2001:db8:1:2::1"), PasswordGuesses.AddressKey("2001:db8:1:3::1"));
        Assert.Equal("203.0.113.9", PasswordGuesses.AddressKey("::ffff:203.0.113.9"));
        Assert.Null(PasswordGuesses.AddressKey("not an address"));
    }

    [Fact]
    public async Task ARefusedLogin_LooksLikeAWrongPassword()
    {
        var users = new InMemoryUserRepository();
        var bcrypt = new BcryptPasswordHasher();
        var md5 = bcrypt.ComputeMd5Hex("password");
        await users.AddAsync(new User { Username = "Player", SafeName = "player", Email = "p@x.dev", PasswordBcrypt = bcrypt.HashPassword(md5) });
        var guesses = new PasswordGuesses(new ManualClock());
        var hasher = new GuardedPasswordHasher(bcrypt, guesses);
        var login = new BanchoLoginHandler(users, hasher, new InMemoryUserStatsRepository(), new PlayerRegistry(), new ChannelRegistry(), NullLogger<BanchoLoginHandler>.Instance);
        string Body(string password) => $"Player\n{password}\nb20250101.1|0|0|abc:runningunderwine:def:ghi:jkl:|0\n";

        var wrong = await login.HandleAsync(Body(bcrypt.ComputeMd5Hex("nope")), "TH", CancellationToken.None, "198.51.100.7");
        for (var i = 0; i < PasswordGuesses.AccountLimit; i++)
            await guesses.FailedAsync(PasswordGuesses.AccountKey((await users.GetBySafeNameAsync("player"))!.PasswordBcrypt), "198.51.100.7", "x" + i);
        var refused = await login.HandleAsync(Body(md5), "TH", CancellationToken.None, "198.51.100.7");

        Assert.Null(refused.Token);
        Assert.Equal(wrong.Body, refused.Body);
    }

    [Fact]
    public async Task ARedisTimeout_LetsTheCheckThrough()
    {
        // RedisTimeoutException isn't a RedisException: a slow Redis used to fail the login or score submission.
        var redis = DispatchProxy.Create<IConnectionMultiplexer, TimingOutRedis>();
        var hasher = new GuardedPasswordHasher(new CountingHasher(), new PasswordGuesses(new ManualClock(), redis));

        Assert.True(await hasher.VerifyAsync(Right, "stored"));
        Assert.False(await hasher.VerifyAsync("wrong", "stored"));
    }

    [Fact]
    public async Task ParallelChecksOfOneCredential_AllGetTheAnswer()
    {
        var hasher = new BcryptPasswordHasher();
        var md5 = hasher.ComputeMd5Hex("password");
        var stored = BCrypt.Net.BCrypt.HashPassword(md5, 4);

        var answers = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => hasher.VerifyAsync(md5, stored).AsTask()));

        Assert.All(answers, Assert.True);
        Assert.False(await hasher.VerifyAsync(hasher.ComputeMd5Hex("nope"), stored));
    }

    private static (GuardedPasswordHasher Hasher, CountingHasher Inner) Guarded(TimeProvider clock)
    {
        var inner = new CountingHasher();
        return (new GuardedPasswordHasher(inner, new PasswordGuesses(clock)), inner);
    }

    /// <summary>A Redis where everything times out (<c>RedisTimeoutException</c> derives from <see cref="TimeoutException"/>).</summary>
    public class TimingOutRedis : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new TimeoutException("Timeout performing SCARD");
    }

    /// <summary>A hasher where only <see cref="Right"/> is right, counting the (would-be bcrypt) checks.</summary>
    private sealed class CountingHasher : IPasswordHasher
    {
        public int Checks { get; private set; }

        public string ComputeMd5Hex(string plaintext) => plaintext;

        public string HashPassword(string passwordMd5Hex) => passwordMd5Hex;

        public ValueTask<bool> VerifyAsync(string passwordMd5Hex, string storedBcrypt, CancellationToken cancellationToken = default)
        {
            Checks++;
            return ValueTask.FromResult(passwordMd5Hex == Right);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
