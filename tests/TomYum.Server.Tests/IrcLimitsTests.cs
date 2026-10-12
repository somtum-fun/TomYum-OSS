using System.Net;
using System.Text;
using TomYum.Server.Irc;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>What an IRC client can't make the server hold: a line without end, or connections without end.</summary>
public class IrcLimitsTests
{
    /// <summary>A stream that hands out its bytes a few at a time, as a slow connection does.</summary>
    private sealed class Trickle(byte[] data, int chunk) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
    }

    [Fact]
    public async Task Lines_AreRead_WithOrWithoutCarriageReturns_InPieces()
    {
        var reader = new IrcLineReader(new Trickle(Encoding.UTF8.GetBytes("NICK Tom_Yum\r\nUSER a b c :d\nPRIVMSG #osu :héllo\r\n"), 3));

        Assert.Equal("NICK Tom_Yum", await reader.ReadLineAsync(default));
        Assert.Equal("USER a b c :d", await reader.ReadLineAsync(default));
        Assert.Equal("PRIVMSG #osu :héllo", await reader.ReadLineAsync(default));
        Assert.Null(await reader.ReadLineAsync(default));
    }

    [Fact]
    public async Task ALineOf512Bytes_IsRead()
    {
        var line = "PRIVMSG #osu :" + new string('a', 512 - 14);
        var reader = new IrcLineReader(new MemoryStream(Encoding.UTF8.GetBytes(line + "\r\nPING x\r\n")));

        Assert.Equal(line, await reader.ReadLineAsync(default));
        Assert.Equal("PING x", await reader.ReadLineAsync(default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALongerLine_EndsTheConnection(bool newlineAtTheEnd)
    {
        // Without a newline the reader stops once a line's worth is in, not when the stream ends.
        var flood = new Trickle(Encoding.UTF8.GetBytes(new string('a', 100_000) + (newlineAtTheEnd ? "\n" : string.Empty)), 700);
        var reader = new IrcLineReader(flood);

        Assert.Null(await reader.ReadLineAsync(default));
        Assert.True(flood.Position < 2_000);
    }

    [Fact]
    public void AnAddress_MayHaveOnlySoManyConnections()
    {
        var limit = new IrcConnectionLimit(2);
        var one = IPAddress.Parse("203.0.113.1");
        var other = IPAddress.Parse("203.0.113.2");

        Assert.True(limit.TryAdd(one));
        Assert.True(limit.TryAdd(one));
        Assert.False(limit.TryAdd(one));
        Assert.True(limit.TryAdd(other));

        limit.Remove(one);
        Assert.True(limit.TryAdd(one));
        Assert.False(limit.TryAdd(one));
    }
}
