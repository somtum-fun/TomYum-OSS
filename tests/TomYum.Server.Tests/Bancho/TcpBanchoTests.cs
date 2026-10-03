using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TomYum.Bancho;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests.Bancho;

/// <summary>Bancho over raw TCP for old builds (docs/old-clients.md).</summary>
public class TcpBanchoTests
{
    [Fact]
    public void ProxyV1_GivesTheClientsAddress()
    {
        var data = Encoding.ASCII.GetBytes("PROXY TCP4 203.0.113.7 10.0.0.2 51234 13381\r\nhello");

        Assert.Equal(ProxyProtocol.Result.Ok, ProxyProtocol.TryRead(data, out var consumed, out var address));
        Assert.Equal(IPAddress.Parse("203.0.113.7"), address);
        Assert.Equal("hello", Encoding.ASCII.GetString(data, consumed, data.Length - consumed));
    }

    [Fact]
    public void ProxyV2_GivesTheClientsAddress()
    {
        byte[] header = [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A, 0x21, 0x11, 0x00, 0x0C,
            198, 51, 100, 9, 10, 0, 0, 2, 0xC8, 0x22, 0x34, 0x25];
        byte[] data = [.. header, 1, 2, 3];

        Assert.Equal(ProxyProtocol.Result.Ok, ProxyProtocol.TryRead(data, out var consumed, out var address));
        Assert.Equal(IPAddress.Parse("198.51.100.9"), address);
        Assert.Equal(header.Length, consumed);

        // Partial headers wait for more; anything else isn't one.
        Assert.Equal(ProxyProtocol.Result.NeedMore, ProxyProtocol.TryRead(header.AsSpan(0, 10), out _, out _));
        Assert.Equal(ProxyProtocol.Result.NeedMore, ProxyProtocol.TryRead("PROXY TCP4 1.2"u8, out _, out _));
        Assert.Equal(ProxyProtocol.Result.Invalid, ProxyProtocol.TryRead("username\r\n"u8, out _, out _));
    }

    [Fact]
    public void TheLogin_IsReadOnceThreeLinesAreIn()
    {
        var buffer = new List<byte>(Encoding.ASCII.GetBytes("Lilian\r\n5f4dcc3b5aa765d61d8327deb882cf99\r\n"));
        Assert.Null(TcpBanchoSession.LoginBody(buffer));

        buffer.AddRange(Encoding.ASCII.GetBytes("b20130801|1|0|a:b:c:d:e:|0\r\n"));
        Assert.Equal("Lilian\n5f4dcc3b5aa765d61d8327deb882cf99\nb20130801|1|0|a:b:c:d:e:|0\n", TcpBanchoSession.LoginBody(buffer));
    }

    [Fact]
    public void Packets_AreTakenWhenComplete()
    {
        var w = new BanchoPacketWriter(64);
        w.WriteInt32Packet(ServerPacketId.UserId, 2);
        w.WriteStringPacket(ServerPacketId.Notification, "hi");
        var two = w.ToArray();

        var buffer = new List<byte>(two);
        buffer.AddRange(two.AsSpan(0, 5)); // the start of a third
        var packets = TcpBanchoSession.TakePackets(buffer);

        Assert.Equal(two, packets);
        Assert.Equal(5, buffer.Count);

        // A length no packet has: the connection is dropped.
        Assert.Null(TcpBanchoSession.TakePackets([1, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F]));
    }

    [Fact]
    public void BuildsBeforeFebruary2013_LogInWithoutTheFriendsOnlyField()
    {
        Assert.True(LoginRequest.TryParse("Lilian\n5f4dcc3b5aa765d61d8327deb882cf99\nb20121225|1|0|a:b:c:d:e:\n", out var request));
        Assert.False(request!.PmPrivate);
        Assert.Equal("b20121225", request.OsuVersion);
    }

    [Theory]
    [InlineData("b337.test|7", -1, 25)]       // maintenance (-5) is "failed" there; the notice is shifted
    [InlineData("b1807|7|0|a:b:c", -5, 24)]
    [InlineData("b20250101|7|0|a:b:c:d:e:|0", -5, 24)]
    public void ARefusedLogin_IsInTheLoginsBuild(string info, int code, int notice)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var refusal = LoginResponseBuilder.BuildFailure(LoginFailureReason.ServerError, "Maintenance.");

        var reply = BanchoExchange.LoginReply(refusal, $"Lilian\n5f4dcc3b5aa765d61d8327deb882cf99\n{info}\n", null, services);

        var r = new BanchoPacketReader(reply);
        Assert.Equal(notice, r.ReadUInt16());
        _ = r.ReadByte();
        r.Skip((int)r.ReadUInt32());
        Assert.Equal(5, r.ReadUInt16());
        _ = r.ReadByte();
        Assert.Equal(4u, r.ReadUInt32());
        Assert.Equal(code, r.ReadInt32());
    }
}
