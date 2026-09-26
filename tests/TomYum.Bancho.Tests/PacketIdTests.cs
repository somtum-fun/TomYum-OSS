using TomYum.Bancho;
using Xunit;

namespace TomYum.Bancho.Tests;

public class PacketIdTests
{
    [Fact]
    public void KnownServerPacketIdsHaveExpectedValues()
    {
        // Scaffold smoke test: locks the wire values learned from the references
        // (see memories/protocols/bancho-stable.md). Expand as packets are implemented.
        Assert.Equal((ushort)5, (ushort)ServerPacketId.UserId);
        Assert.Equal((ushort)7, (ushort)ServerPacketId.SendMessage);
        Assert.Equal((ushort)83, (ushort)ServerPacketId.UserPresence);
        Assert.Equal((ushort)11, (ushort)ServerPacketId.UserStats);
    }

    [Fact]
    public void KnownClientPacketIdsHaveExpectedValues()
    {
        Assert.Equal((ushort)0, (ushort)ClientPacketId.ChangeAction);
        Assert.Equal((ushort)1, (ushort)ClientPacketId.SendPublicMessage);
        Assert.Equal((ushort)2, (ushort)ClientPacketId.Logout);
        Assert.Equal((ushort)85, (ushort)ClientPacketId.UserStatsRequest);
    }
}
