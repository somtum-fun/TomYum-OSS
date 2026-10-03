using System.Net;
using System.Net.Sockets;

namespace TomYum.Gateway;

/// <summary>Accepts TCP connections on some ports and hands each to <see cref="HandleAsync"/>, a few per address at most.</summary>
public abstract class TcpGatewayService : BackgroundService
{
    private readonly ConnectionLimiter _limiter;

    protected TcpGatewayService(ConnectionLimiter limiter, ILogger logger)
    {
        _limiter = limiter;
        Logger = logger;
    }

    protected ILogger Logger { get; }

    /// <summary>What this serves, for the logs.</summary>
    protected abstract string Name { get; }

    protected abstract IReadOnlyList<int> Ports { get; }

    /// <summary>Serves one connection from <paramref name="address"/> until it ends.</summary>
    protected abstract Task HandleAsync(TcpClient client, IPAddress address, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = new List<Task>();
        foreach (var port in Ports)
        {
            var listener = new TcpListener(IPAddress.IPv6Any, port);
            listener.Server.DualMode = true;
            try
            {
                listener.Start();
            }
            catch (SocketException ex)
            {
                Logger.LogError(ex, "{Name}: can't listen on port {Port}.", Name, port);
                continue;
            }
            Logger.LogInformation("{Name}: listening on port {Port}.", Name, port);
            loops.Add(AcceptAsync(listener, stoppingToken));
        }
        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address is { } remote
                    ? (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)
                    : IPAddress.None;
                if (!_limiter.TryEnter(address))
                {
                    Logger.LogWarning("{Name}: refused {Address}, too many connections from it.", Name, address);
                    client.Dispose();
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (client)
                        {
                            client.NoDelay = true;
                            await HandleAsync(client, address, stoppingToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or HttpRequestException)
                    {
                        Logger.LogDebug(ex, "{Name}: connection from {Address} ended.", Name, address);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "{Name}: connection from {Address} failed.", Name, address);
                    }
                    finally
                    {
                        _limiter.Leave(address);
                    }
                }, CancellationToken.None);
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
