using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TomYum.Server.Gateway;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>The gateway link: the player's address is taken from the gateway only with its key.</summary>
public sealed class GatewayLinkTests
{
    private const string Key = "a-long-shared-secret";

    [Fact]
    public async Task With_the_key_the_players_address_replaces_the_gateways()
    {
        var context = await RunAsync(new GatewayOptions { Key = Key }, Key, "203.0.113.7");

        Assert.Equal("203.0.113.7", context.Request.Headers["X-Real-IP"].ToString());
        Assert.False(context.Request.Headers.ContainsKey("CF-IPCountry"));
        Assert.False(context.Request.Headers.ContainsKey(GatewayLink.ClientIpHeader));
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("")]
    public async Task Without_the_key_the_claimed_address_is_ignored(string sentKey)
    {
        var context = await RunAsync(new GatewayOptions { Key = Key }, sentKey, "203.0.113.7");

        Assert.Equal("198.51.100.1", context.Request.Headers["X-Real-IP"].ToString());
        Assert.Equal("SG", context.Request.Headers["CF-IPCountry"].ToString());
        Assert.False(context.Request.Headers.ContainsKey(GatewayLink.ClientIpHeader));
    }

    [Fact]
    public async Task With_the_link_off_nothing_is_trusted()
    {
        var context = await RunAsync(new GatewayOptions(), "", "203.0.113.7");

        Assert.Equal("198.51.100.1", context.Request.Headers["X-Real-IP"].ToString());
    }

    [Fact]
    public async Task A_claimed_address_that_isnt_one_is_ignored()
    {
        var context = await RunAsync(new GatewayOptions { Key = Key }, Key, "not-an-ip");

        Assert.Equal("198.51.100.1", context.Request.Headers["X-Real-IP"].ToString());
    }

    private static async Task<HttpContext> RunAsync(GatewayOptions options, string sentKey, string claimedIp)
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseGatewayLink(options);
        app.Run(_ => Task.CompletedTask);

        var context = new DefaultHttpContext();
        context.Request.Headers["X-Real-IP"] = "198.51.100.1";
        context.Request.Headers["CF-IPCountry"] = "SG";
        if (sentKey.Length > 0)
            context.Request.Headers[GatewayLink.KeyHeader] = sentKey;
        context.Request.Headers[GatewayLink.ClientIpHeader] = claimedIp;
        await app.Build()(context);
        return context;
    }
}
