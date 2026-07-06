using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// Everything a <see cref="IClientPacketHandler"/> needs to handle one inbound packet:
/// the originating <see cref="OnlinePlayer"/>, the online registry, the decoded payload,
/// and a shared <see cref="BanchoPacketWriter"/> for any packets to send straight back to
/// the originator in this poll's response.
/// </summary>
/// <remarks>
/// The payload is exposed as <see cref="ReadOnlyMemory{T}"/> (not a span) so the context can
/// live as a normal class; a handler slices a fresh <see cref="BanchoPacketReader"/> over
/// <see cref="Payload"/>'s span when it needs to read fields. Packets destined for
/// <em>other</em> players are queued on those players via <see cref="PlayerRegistry"/>;
/// packets for the originator go through <see cref="Response"/>.
/// Handlers that require scoped services (e.g. repositories) resolve them from
/// <see cref="Services"/> using <c>GetRequiredService&lt;T&gt;()</c>.
/// </remarks>
public sealed class ClientPacketContext
{
    /// <summary>Creates a context for one packet handled on behalf of <paramref name="player"/>.</summary>
    public ClientPacketContext(
        OnlinePlayer player,
        PlayerRegistry registry,
        ChannelRegistry channels,
        MatchRegistry matches,
        IServiceProvider services,
        ReadOnlyMemory<byte> payload,
        BanchoPacketWriter response)
    {
        Player = player;
        Registry = registry;
        Channels = channels;
        Matches = matches;
        Services = services;
        Payload = payload;
        Response = response;
    }

    /// <summary>The player whose poll this packet arrived on.</summary>
    public OnlinePlayer Player { get; }

    /// <summary>The online-player registry (lookups + broadcasts).</summary>
    public PlayerRegistry Registry { get; }

    /// <summary>The chat-channel registry (membership + routing).</summary>
    public ChannelRegistry Channels { get; }

    /// <summary>The multiplayer match registry.</summary>
    public MatchRegistry Matches { get; }

    /// <summary>
    /// The request-scoped DI container. Handlers that need DB access resolve repositories
    /// here (e.g. <c>Services.GetRequiredService&lt;IUserStatsRepository&gt;()</c>).
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>The raw payload bytes for this packet (header already consumed).</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Writer for packets sent back to <see cref="Player"/> in this response.</summary>
    public BanchoPacketWriter Response { get; }
}
