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

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                var session = ActivatorUtilities.CreateInstance<IrcSession>(_services, client);
                _ = Task.Run(async () =>
                {
                    using (session)
                        await session.RunAsync(stoppingToken).ConfigureAwait(false);
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
