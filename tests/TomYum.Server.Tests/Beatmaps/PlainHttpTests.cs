using Microsoft.AspNetCore.Http;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>Telling an old client (plain HTTP) from today's (HTTPS) behind the proxies (docs/old-clients.md).</summary>
public class PlainHttpTests
{
    [Theory]
    // Behind Cloudflare: the scheme the client used, whatever the proxies after it say.
    [InlineData("{\"scheme\":\"http\"}", "http", true)]
    [InlineData("{\"scheme\":\"https\"}", "http", false)]
    // Behind Caddy alone (local testing).
    [InlineData(null, "http", true)]
    [InlineData(null, "https", false)]
    [InlineData(null, "http, https", true)]
    // No proxy header: today's client.
    [InlineData(null, null, false)]
    public void ThePlainHttpClient_IsTheOldOne(string? cfVisitor, string? forwardedProto, bool expected)
    {
        var request = new DefaultHttpContext().Request;
        if (cfVisitor is not null)
            request.Headers["Cf-Visitor"] = cfVisitor;
        if (forwardedProto is not null)
            request.Headers["X-Forwarded-Proto"] = forwardedProto;

        Assert.Equal(expected, DirectEndpoints.CameOverPlainHttp(request));
    }
}
