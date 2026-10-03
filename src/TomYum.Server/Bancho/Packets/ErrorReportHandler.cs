using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets;

/// <summary>ERROR_REPORT (20): an old build's exception text (docs/old-clients.md), into the server log.</summary>
public sealed class ErrorReportHandler : IClientPacketHandler
{
    private readonly ILogger<ErrorReportHandler> _logger;

    public ErrorReportHandler(ILogger<ErrorReportHandler> logger) => _logger = logger;

    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.ErrorReport;

    /// <inheritdoc />
    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        string text;
        try
        {
            text = new BanchoPacketReader(context.Payload.Span).ReadString();
        }
        catch (InvalidDataException)
        {
            return Task.CompletedTask;
        }
        _logger.LogWarning("Client error from {User} on {Version}: {Error}",
            context.Player.UserId, context.Player.Build.Version, text.Length > 2000 ? text[..2000] : text);
        return Task.CompletedTask;
    }
}
