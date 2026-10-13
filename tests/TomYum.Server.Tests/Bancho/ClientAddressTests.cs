using System.Net;
using Microsoft.AspNetCore.Http;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests.Bancho;

/// <summary>Whose address a request is: forwarding headers count only from our own proxy.</summary>
public sealed class ClientAddressTests
{
    [Theory]
    [InlineData("127.0.0.1", "198.51.100.7")]   // the stack's Caddy on this machine
    [InlineData("10.89.0.5", "198.51.100.7")]   // ... or on the compose network
    [InlineData("172.18.0.3", "198.51.100.7")]
    [InlineData("fd00::5", "198.51.100.7")]
    [InlineData("203.0.113.9", "203.0.113.9")]  // straight from the internet: the header is the client's own claim
    public void TheForwardedAddress_IsBelievedOnlyFromOurProxy(string peer, string expected)
    {
        var context = Request(peer, ("X-Real-IP", "198.51.100.7"), ("CF-IPCountry", "TH"));

        Assert.Equal(expected, LoginIps.ClientAddress(context));
        Assert.Equal(expected == peer ? string.Empty : "TH", LoginIps.ProxyHeader(context, "CF-IPCountry"));
    }

    [Fact]
    public void TheGatewaysAddress_WinsWhenItVouchedForIt()
    {
        var context = Request("203.0.113.9", ("X-Real-IP", "198.51.100.7"));
        context.Items[LoginIps.GatewayClientItem] = "192.0.2.44";

        Assert.Equal("192.0.2.44", LoginIps.ClientAddress(context));
    }

    private static DefaultHttpContext Request(string peer, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        foreach (var (name, value) in headers)
            context.Request.Headers[name] = value;
        return context;
    }
}
