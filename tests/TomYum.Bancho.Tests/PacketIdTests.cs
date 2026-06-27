using TomYum.Bancho;
using Xunit;

namespace TomYum.Bancho.Tests;

public class PacketIdTests
{
    [Fact]
    public void Placeholder_KnownPacketIdsHaveExpectedValues()
    {
        // Scaffold smoke test: locks the wire values learned from the references
        // (see memories/protocols/bancho-stable.md). Expand as packets are implemented.
        Assert.Equal((ushort)5, (ushort)PacketId.ServerUserId);
        Assert.Equal((ushort)7, (ushort)PacketId.ServerSendMessage);
    }
}
