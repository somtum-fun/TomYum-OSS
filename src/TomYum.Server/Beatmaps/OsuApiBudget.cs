namespace TomYum.Server.Beatmaps;

/// <summary>
/// A pace for requests to one service: at most <see cref="PerMinute"/> in any minute, the rest wait
/// their turn (in order). One instance is shared by everything that asks osu!'s API (osu! asks
/// applications for at most 60 requests a minute and may revoke keys that go over), and another by
/// the beatmap mirror, so no single feature (imports while browsing lazer, circleguard, map updates)
/// can use up the key for everyone.
/// </summary>
public sealed class OsuApiBudget : IDisposable
{
    /// <summary>osu!'s documented limit is 60 a minute; this leaves room for bursts and clock skew.</summary>
    public const int DefaultPerMinute = 50;

    private readonly TimeSpan _window;

    private readonly TimeProvider _time;
    private readonly Queue<DateTimeOffset> _sent = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="time">The clock.</param>
    /// <param name="perMinute">Requests allowed per window.</param>
    /// <param name="window">The window (a minute; shorter in tests).</param>
    public OsuApiBudget(TimeProvider time, int perMinute = DefaultPerMinute, TimeSpan? window = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perMinute, 1);
        _time = time;
        PerMinute = perMinute;
        _window = window ?? TimeSpan.FromMinutes(1);
    }

    public int PerMinute { get; }

    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    /// <summary>Whether the service asked us to stop for now (see <see cref="Pause"/>).</summary>
    public bool IsPaused => _time.GetUtcNow() < _pausedUntil;

    /// <summary>When a pause ends.</summary>
    public DateTimeOffset PausedUntil => _pausedUntil;

    /// <summary>No requests until <paramref name="until"/> (a 429's Retry-After, a block).</summary>
    public void Pause(DateTimeOffset until)
    {
        if (until > _pausedUntil)
            _pausedUntil = until;
    }

    /// <summary>Requests made in the last minute.</summary>
    public int UsedLastMinute
    {
        get
        {
            lock (_sent)
            {
                Forget(_time.GetUtcNow());
                return _sent.Count;
            }
        }
    }

    /// <summary>Waits until a request may be made, then counts it.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                TimeSpan wait;
                lock (_sent)
                {
                    var now = _time.GetUtcNow();
                    Forget(now);
                    if (_sent.Count < PerMinute)
                    {
                        _sent.Enqueue(now);
                        return;
                    }
                    wait = _sent.Peek() + _window - now;
                }
                await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1), _time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private void Forget(DateTimeOffset now)
    {
        while (_sent.Count > 0 && now - _sent.Peek() >= _window)
            _sent.Dequeue();
    }
}

/// <summary>
/// Makes every request to osu!'s API (<c>/api/</c> and <c>/oauth/</c> on osu.ppy.sh and old.ppy.sh)
/// through the HTTP client it's on wait for the shared <see cref="OsuApiBudget"/>. Other requests
/// (.osu files, assets) pass straight through.
/// </summary>
public sealed class OsuApiBudgetHandler : DelegatingHandler
{
    private readonly OsuApiBudget _budget;

    public OsuApiBudgetHandler(OsuApiBudget budget)
    {
        _budget = budget;
    }

    public static bool IsOsuApi(Uri? uri) =>
        uri is { } u
        && (u.Host.Equals("osu.ppy.sh", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("old.ppy.sh", StringComparison.OrdinalIgnoreCase))
        && (u.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal) || u.AbsolutePath.StartsWith("/oauth/", StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsOsuApi(request.RequestUri))
            await _budget.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
