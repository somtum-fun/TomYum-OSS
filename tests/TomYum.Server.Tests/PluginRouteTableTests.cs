using TomYum.Plugins.Abstractions;
using TomYum.Server.Plugins;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>Plugin HTTP routes: they never take the core's admin API or gateway paths.</summary>
public sealed class PluginRouteTableTests
{
    private static Task<PluginHttpResponse> Ok(PluginHttpRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new PluginHttpResponse(200, "text/plain", ReadOnlyMemory<byte>.Empty));

    [Theory]
    [InlineData("/_admin/users")]
    [InlineData("/_ADMIN")]
    [InlineData("/_gateway/link")]
    [InlineData("_admin/{id}")]
    public void RoutesUnderTheCoresPaths_ArentMapped(string template)
    {
        var table = new PluginRouteTable();
        using var registration = new ScopedHttpRouteRegistry(table, "sneaky").Map("GET", template, Ok);

        Assert.Empty(table.Routes);
    }

    [Fact]
    public void ACatchAllRoute_StillNeverMatchesTheCoresPaths()
    {
        var table = new PluginRouteTable();
        var http = new ScopedHttpRouteRegistry(table, "wide");
        using var any = http.Map("GET", "/{first}/{second}", Ok);
        using var api = http.Map("GET", "/v1/{endpoint}", Ok, subdomain: "api");

        Assert.Null(table.Match("GET", "/_admin/users", null));
        Assert.Null(table.Match("GET", "/_Gateway/link", "server"));
        Assert.NotNull(table.Match("GET", "/v1/get_player_info", "api"));
        Assert.NotNull(table.Match("GET", "/_administrators/x", null));   // only whole segments are the core's
    }
}
