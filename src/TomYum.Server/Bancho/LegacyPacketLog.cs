using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace TomYum.Server.Bancho;

/// <summary>
/// For old builds (docs/old-clients.md): logs each packet type the first time it goes to or comes
/// from the session, so what an old client gets and asks for shows in the server log without a
/// packet capture. Today's clients aren't logged.
/// </summary>
public static class LegacyPacketLog
{
    private const string SeenKey = "tomyum.legacy-packets-seen";

    /// <summary>Notes the packets in <paramref name="stream"/> (framed bancho packets) for <paramref name="player"/>.</summary>
    public static void Note(OnlinePlayer player, ReadOnlySpan<byte> stream, bool outgoing, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(logger);
        if (!player.Build.IsLegacy || stream.IsEmpty)
            return;

        var seen = player.GetState<ConcurrentDictionary<int, bool>>(SeenKey);
        if (seen is null)
        {
            seen = new ConcurrentDictionary<int, bool>();
            player.SetState(SeenKey, seen);
        }

        var offset = 0;
        while (offset + 7 <= stream.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(stream[offset..]);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(stream[(offset + 3)..]);
            // Outgoing ids as they are, incoming ones offset so both directions are told apart.
            if (seen.TryAdd(outgoing ? id : -1 - id, true))
            {
                logger.LogInformation("Old client {User} on {Version}: first {Direction} packet {PacketId} ({Length} bytes).",
                    player.UserId, player.Build.Version, outgoing ? "outgoing" : "incoming", id, length);
            }
            if (length < 0 || offset + 7 + length > stream.Length)
                break;
            offset += 7 + length;
        }
    }
}
