using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>One count per play: the same score sent twice, and two plays stored at once (without Redis).</summary>
public class ScoreSubmissionGuardTests
{
    private const string Line = "md5:alice:checksum:300:0:0:0:0:0:1000000:500:True:X:0:True:0:261011120000:20240123";

    [Fact]
    public async Task The_same_score_is_taken_once()
    {
        var guard = new ScoreSubmissionGuard(new Clock());
        var fingerprint = ScoreSubmissionGuard.Fingerprint(Line);
        Assert.True(await guard.TryClaimAsync(fingerprint));
        Assert.False(await guard.TryClaimAsync(fingerprint));
        Assert.True(await guard.TryClaimAsync(ScoreSubmissionGuard.Fingerprint(Line.Replace("checksum", "another", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task A_score_that_wasnt_stored_can_be_sent_again()
    {
        var guard = new ScoreSubmissionGuard(new Clock());
        var fingerprint = ScoreSubmissionGuard.Fingerprint(Line);
        Assert.True(await guard.TryClaimAsync(fingerprint));
        await guard.ReleaseAsync(fingerprint);
        Assert.True(await guard.TryClaimAsync(fingerprint));
    }

    [Fact]
    public async Task A_score_is_forgotten_after_a_while()
    {
        var clock = new Clock();
        var guard = new ScoreSubmissionGuard(clock);
        var fingerprint = ScoreSubmissionGuard.Fingerprint(Line);
        Assert.True(await guard.TryClaimAsync(fingerprint));
        clock.Now += ScoreSubmissionGuard.RememberedFor - TimeSpan.FromMinutes(1);
        Assert.False(await guard.TryClaimAsync(fingerprint));
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.True(await guard.TryClaimAsync(fingerprint));
    }

    [Fact]
    public async Task Two_identical_scores_at_once_are_taken_once()
    {
        var guard = new ScoreSubmissionGuard(new Clock());
        var fingerprint = ScoreSubmissionGuard.Fingerprint(Line);
        var claims = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => guard.TryClaimAsync(fingerprint))));
        Assert.Single(claims, taken => taken);
    }

    [Fact]
    public async Task Plays_on_one_map_are_stored_one_at_a_time()
    {
        var guard = new ScoreSubmissionGuard(new Clock());
        var key = ScoreSubmissionGuard.BestKey(5, "md5", 0);

        var first = await guard.LockAsync(key);
        var second = guard.LockAsync(key);
        // Another map, mode or player isn't held up.
        using (await guard.LockAsync(ScoreSubmissionGuard.BestKey(5, "md5", 4)))
        using (await guard.LockAsync(ScoreSubmissionGuard.BestKey(6, "md5", 0)))
        {
            await Task.Delay(50);
            Assert.False(second.IsCompleted);
        }

        first.Dispose();
        using (await second.WaitAsync(TimeSpan.FromSeconds(5)))
            Assert.Equal(1, guard.HeldKeys);
        Assert.Equal(0, guard.HeldKeys);
    }

    [Fact]
    public async Task Concurrent_plays_never_overlap()
    {
        var guard = new ScoreSubmissionGuard(new Clock());
        var key = ScoreSubmissionGuard.BestKey(5, "md5", 0);
        var inside = 0;
        var most = 0;
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            using (await guard.LockAsync(key))
            {
                var now = Interlocked.Increment(ref inside);
                InterlockedMax(ref most, now);
                await Task.Delay(2);
                Interlocked.Decrement(ref inside);
            }
        })));
        Assert.Equal(1, most);
        Assert.Equal(0, guard.HeldKeys);
    }

    [Fact]
    public async Task A_cancelled_wait_leaves_nothing_held()
    {
        var guard = new ScoreSubmissionGuard(new Clock());
        var key = ScoreSubmissionGuard.BestKey(5, "md5", 0);
        using (await guard.LockAsync(key))
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guard.LockAsync(key, cancel.Token));
        }
        Assert.Equal(0, guard.HeldKeys);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
