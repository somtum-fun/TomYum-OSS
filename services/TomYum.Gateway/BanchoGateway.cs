using System.Net;
using System.Net.Sockets;

namespace TomYum.Gateway;

/// <summary>
/// Bancho over TCP for builds before HTTP bancho (pre-2014; <c>server.&lt;domain&gt;:13381</c>, like
/// <c>server.titanic.sh</c>). The packets are the same as HTTP bancho's; only the transport differs.
/// So each connection becomes an HTTP bancho session on the main server: the three-line login
/// is POSTed as a login, the <c>cho-token</c> it returns is kept, and from then on whatever the
/// client sends goes out in the next poll and whatever comes back is written to the socket.
/// </summary>
public sealed class BanchoGateway : TcpGatewayService
{
    private const int MaxLoginBytes = 8 * 1024;
    private const int MaxPendingBytes = 1024 * 1024;
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(20);

    /// <summary>A server ping (id 8, empty): what bancho sent TCP clients when there was nothing else.</summary>
    private static readonly byte[] PingPacket = [8, 0, 0, 0, 0, 0, 0];

    /// <summary>The client's "exiting" packet (id 2, an int32 0), sent for it when the socket drops.</summary>
    private static readonly byte[] ExitPacket = [2, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0];

    private readonly GatewayOptions _options;
    private readonly Upstream _upstream;

    public BanchoGateway(GatewayOptions options, Upstream upstream, ConnectionLimiter limiter, ILogger<BanchoGateway> logger)
        : base(limiter, logger)
    {
        _options = options;
        _upstream = upstream;
    }

    protected override string Name => "Bancho (TCP)";

    protected override IReadOnlyList<int> Ports => _options.Bancho.Ports;

    protected override async Task HandleAsync(TcpClient client, IPAddress address, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();

        Logger.LogInformation("Bancho (TCP): connection from {Address}.", address);
        var received = new MemoryStream();
        string? missing;
        using (var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            loginTimeout.CancelAfter(LoginTimeout);
            missing = await ReadLoginAsync(stream, received, loginTimeout.Token).ConfigureAwait(false);
        }
        // Old builds write the login with Windows line ends (StreamWriter.WriteLine); HTTP bancho's
        // login is plain \n lines, and a \r left on the username makes it unknown.
        var login = received.ToArray().Where(b => b != (byte)'\r').ToArray();
        if (missing is not null)
        {
            // What came instead of a login, to see what a build sends (the password is an md5 hash).
            Logger.LogInformation("Bancho (TCP): no login from {Address} ({Reason}, {Count} bytes): {Start}",
                address, missing, login.Length, Convert.ToHexString(login.AsSpan(0, Math.Min(login.Length, 96))));
            return;
        }

        string? token;
        using (var request = _upstream.BanchoRequest(address, login, token: null))
        using (var response = await _upstream.Http.SendAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning("Bancho (TCP): login from {Address} got HTTP {Status} from the main server.", address, (int)response.StatusCode);
                return;
            }
            token = response.Headers.TryGetValues("cho-token", out var tokens) ? tokens.FirstOrDefault() : null;
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        // Declined (wrong password, held for verification, ...): the reason is in what was just
        // written. Give the client a moment to show it before closing.
        if (string.IsNullOrEmpty(token) || token == "no-token")
        {
            Logger.LogInformation("Bancho (TCP): the main server declined the login from {Address}.", address);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            return;
        }
        Logger.LogInformation("Bancho (TCP): session opened for {Address}.", address);

        using var session = new Session(stream);
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reading = session.ReadAsync(ended.Token);
        try
        {
            await PollAsync(session, address, token, ended.Token).ConfigureAwait(false);
        }
        finally
        {
            await ended.CancelAsync().ConfigureAwait(false);
            try
            {
                await reading.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }

            // Whatever the client still had queued, then its exit, so the player doesn't linger online.
            if (!session.SentExit)
                await TryPostAsync(address, token, [.. session.Take(), .. ExitPacket]).ConfigureAwait(false);
            Logger.LogInformation("Bancho (TCP): session closed for {Address}.", address);
        }
    }

    private async Task PollAsync(Session session, IPAddress address, string token, CancellationToken cancellationToken)
    {
        var busy = TimeSpan.FromMilliseconds(Math.Max(10, _options.Bancho.BusyPollMs));
        var idle = TimeSpan.FromMilliseconds(Math.Max(busy.TotalMilliseconds, _options.Bancho.IdlePollMs));
        var keepAlive = TimeSpan.FromSeconds(Math.Max(1, _options.Bancho.KeepAliveSeconds));
        var lastWrite = DateTime.UtcNow;
        var wait = busy;
        while (!cancellationToken.IsCancellationRequested && !session.Closed)
        {
            await session.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            if (session.Pending > MaxPendingBytes)
            {
                Logger.LogWarning("Bancho (TCP): {Address} sent more than the main server takes in; closing.", address);
                return;
            }

            var payload = session.Take();
            LogErrorReports(payload, address);
            using var request = _upstream.BanchoRequest(address, payload, token);
            using var response = await _upstream.Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning("Bancho (TCP): poll for {Address} got HTTP {Status}; closing.", address, (int)response.StatusCode);
                return;
            }
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > 0)
            {
                await session.Stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                lastWrite = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - lastWrite >= keepAlive)
            {
                // TCP clients reconnect when bancho says nothing for a while (b1122: 30 s); HTTP
                // bancho never pings, so ping them here. They answer with a pong.
                await session.Stream.WriteAsync(PingPacket, cancellationToken).ConfigureAwait(false);
                lastWrite = DateTime.UtcNow;
            }
            if (session.SentExit)
                return;

            // Ask again soon while things are happening, slowly while they aren't.
            wait = payload.Length > 0 || body.Length > 0 ? busy : idle;
        }
    }

    private async Task TryPostAsync(IPAddress address, string token, byte[] payload)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = _upstream.BanchoRequest(address, payload, token);
            using var response = await _upstream.Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            Logger.LogDebug(ex, "Bancho (TCP): couldn't tell the main server {Address} left.", address);
        }
    }

    /// <summary>
    /// Old builds send their own exception when they can't read a packet (id 21, a string, then
    /// reconnect); the main server ignores it, so it's logged here: it names the packet that broke them.
    /// </summary>
    private void LogErrorReports(byte[] payload, IPAddress address)
    {
        var offset = 0;
        while (offset + 7 <= payload.Length)
        {
            var id = payload[offset] | (payload[offset + 1] << 8);
            var length = BitConverter.ToInt32(payload, offset + 3);
            if (id == 21 && length > 1 && payload[offset + 7] == 0x0b)
            {
                var (textLength, start) = ReadUleb128(payload, offset + 8);
                if (start + textLength <= offset + 7 + length)
                    Logger.LogWarning("Bancho (TCP): {Address}'s client reported an error:\n{Report}", address, System.Text.Encoding.UTF8.GetString(payload, start, textLength));
            }
            offset += 7 + length;
        }
    }

    private static (int Value, int Next) ReadUleb128(byte[] bytes, int offset)
    {
        int value = 0, shift = 0;
        while (offset < bytes.Length)
        {
            var b = bytes[offset++];
            value |= (b & 0x7f) << shift;
            if ((b & 0x80) == 0)
                break;
            shift += 7;
        }
        return (value, offset);
    }

    /// <summary>Reads the login (three lines) into <paramref name="received"/>; null when it all came, else why not.</summary>
    private static async Task<string?> ReadLoginAsync(NetworkStream stream, MemoryStream received, CancellationToken cancellationToken)
    {
        var one = new byte[1];
        var lines = 0;
        try
        {
            while (lines < 3)
            {
                if (received.Length >= MaxLoginBytes)
                    return "too long";
                if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
                    return "closed";
                received.WriteByte(one[0]);
                if (one[0] == (byte)'\n')
                    lines++;
            }
        }
        catch (OperationCanceledException)
        {
            return "timed out";
        }
        return null;
    }

    /// <summary>One connection: what the client has sent and not yet passed on.</summary>
    private sealed class Session : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly SemaphoreSlim _arrived = new(0);
        private MemoryStream _pending = new();

        public Session(NetworkStream stream) => Stream = stream;

        public void Dispose()
        {
            _arrived.Dispose();
            _pending.Dispose();
        }

        public NetworkStream Stream { get; }

        public bool Closed { get; private set; }

        /// <summary>Whether the client's own exit packet went (or is about to go) to the main server.</summary>
        public bool SentExit { get; private set; }

        public long Pending
        {
            get
            {
                lock (_gate)
                    return _pending.Length;
            }
        }

        public async Task ReadAsync(CancellationToken cancellationToken)
        {
            var chunk = new byte[8 * 1024];
            try
            {
                while (true)
                {
                    var read = await Stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        return;
                    lock (_gate)
                        _pending.Write(chunk, 0, read);
                    _arrived.Release();
                }
            }
            finally
            {
                Closed = true;
                _arrived.Release();
            }
        }

        /// <summary>Waits for the client to send something, at most <paramref name="timeout"/>.</summary>
        public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (await _arrived.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
            {
                // Let the rest of a burst arrive so it goes in one request.
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                while (_arrived.CurrentCount > 0)
                    await _arrived.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// The whole packets received so far; a packet still arriving stays for the next request
        /// (TCP may split one anywhere, and the main server reads each request on its own).
        /// </summary>
        public byte[] Take()
        {
            lock (_gate)
            {
                var bytes = _pending.ToArray();
                var (whole, exit) = Scan(bytes);
                _pending = new MemoryStream();
                _pending.Write(bytes, whole, bytes.Length - whole);
                SentExit |= exit;
                return bytes[..whole];
            }
        }

        /// <summary>How many bytes of <paramref name="bytes"/> are whole packets, and whether one is the client's exit (id 2).</summary>
        private static (int Whole, bool Exit) Scan(ReadOnlySpan<byte> bytes)
        {
            var offset = 0;
            var exit = false;
            while (offset + 7 <= bytes.Length)
            {
                var id = bytes[offset] | (bytes[offset + 1] << 8);
                var length = BitConverter.ToInt32(bytes.Slice(offset + 3, 4));
                if (length < 0 || offset + 7L + length > bytes.Length)
                    break;
                exit |= id == 2;
                offset += 7 + length;
            }
            return (offset, exit);
        }
    }
}
