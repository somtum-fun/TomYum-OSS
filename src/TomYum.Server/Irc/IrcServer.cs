using System.Net;
using System.Net.Sockets;

namespace TomYum.Server.Irc;

/// <summary>Settings for the IRC server (section <c>Irc</c>).</summary>
public sealed class IrcOptions
{
    public const string SectionName = "Irc";

    /// <summary>Whether it runs (default yes).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The TCP port (osu!'s is 6667).</summary>
    public int Port { get; set; } = 6667;

    /// <summary>The server's name in replies, e.g. <c>irc.sundei.eu</c>; defaults to <c>irc.&lt;Server:Domain&gt;</c>.</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>With website accounts: the account settings page where players make their IRC key.</summary>
    public string? KeyPageUrl { get; set; }

    /// <summary>How many connections one address may have open at once (a player needs one; a few bots more).</summary>
    public int MaxConnectionsPerAddress { get; set; } = 5;
}

/// <summary>The connections open from each address, up to a limit.</summary>
internal sealed class IrcConnectionLimit(int limit)
{
    private readonly Dictionary<IPAddress, int> _open = [];

    /// <summary>Counts a connection from <paramref name="address"/>; false (not counted) when it has as many as it may.</summary>
    public bool TryAdd(IPAddress address)
    {
        lock (_open)
        {
            _open.TryGetValue(address, out var count);
            if (count >= limit)
                return false;
            _open[address] = count + 1;
            return true;
        }
    }

    /// <summary>A connection from <paramref name="address"/> closed.</summary>
    public void Remove(IPAddress address)
    {
        lock (_open)
        {
            if (_open.TryGetValue(address, out var count) && count > 1)
                _open[address] = count - 1;
            else
                _open.Remove(address);
        }
    }
}

/// <summary>
/// osu!-style IRC (like <c>irc.ppy.sh:6667</c>): chat and bots from any IRC client, in the same
/// channels and PMs as the game, the website, lazer and Discord. Each connection is an
/// <see cref="IrcSession"/>. Players log in with their username and the password BanchoBot's
/// <c>!irc</c> gives them.
/// </summary>
public sealed class IrcServer : BackgroundService
{
    private readonly IrcOptions _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<IrcServer> _logger;

    public IrcServer(IrcOptions options, IServiceProvider services, ILogger<IrcServer> logger)
    {
        _options = options;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Any, _options.Port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "IRC: can't listen on port {Port}; IRC is off.", _options.Port);
            return;
        }
        _logger.LogInformation("IRC: listening on port {Port} as {Name}.", _options.Port, _options.ServerName);
        var limit = new IrcConnectionLimit(_options.MaxConnectionsPerAddress);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address is { } remote
                    ? remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote
                    : IPAddress.None;
                if (!limit.TryAdd(address))
                {
                    _logger.LogInformation("IRC: refused a connection from {Address}: it has {Max} open already.", address, _options.MaxConnectionsPerAddress);
                    client.Dispose();
                    continue;
                }
                var session = ActivatorUtilities.CreateInstance<IrcSession>(_services, (Stream)client.GetStream());
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (client)
                        using (session)
                            await session.RunAsync(stoppingToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        limit.Remove(address);
                    }
                }, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
        }
    }
}
