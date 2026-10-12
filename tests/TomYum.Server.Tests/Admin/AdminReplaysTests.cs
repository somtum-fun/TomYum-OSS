using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Server.Admin;
using TomYum.Server.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.Admin;

/// <summary>A signed admin API request that changes something works once; sent again, it's refused.</summary>
public sealed class AdminReplaysTests
{
    private const string Secret = "shared-secret";

    [Fact]
    public async Task ASignedPost_WorksOnce()
    {
        var filter = Filter();
        var path = "/_admin/users/5/restrict?timestamp=" + Now();

        Assert.Equal("ran", await SendAsync(filter, "POST", path, """{"reason":"x"}"""));
        Assert.IsType<UnauthorizedHttpResult>(await SendAsync(filter, "POST", path, """{"reason":"x"}"""));
        Assert.Equal("ran", await SendAsync(filter, "POST", path, """{"reason":"y"}"""));   // another body: another request
    }

    [Fact]
    public async Task TheSameGet_InOneSecond_StillWorks_UnlessItCarriesANonce()
    {
        var filter = Filter();
        var path = "/_admin/rankings/osu?timestamp=" + Now();

        Assert.Equal("ran", await SendAsync(filter, "GET", path, string.Empty));
        Assert.Equal("ran", await SendAsync(filter, "GET", path, string.Empty));

        var once = path + "&nonce=abc123";
        Assert.Equal("ran", await SendAsync(filter, "GET", once, string.Empty));
        Assert.IsType<UnauthorizedHttpResult>(await SendAsync(filter, "GET", once, string.Empty));
    }

    [Fact]
    public async Task AUsedSignature_IsForgottenAfterTenMinutes()
    {
        var clock = new ManualClock();
        var replays = new AdminReplays(clock);

        Assert.True(await replays.TryUseAsync("abc"));
        Assert.False(await replays.TryUseAsync("ABC"));
        clock.Advance(AdminReplays.KeptFor + TimeSpan.FromSeconds(1));
        Assert.True(await replays.TryUseAsync("abc"));
    }

    private static AdminAuthFilter Filter() =>
        new(new OsuWebInteropOptions { InteropSecret = Secret }, TimeProvider.System, NullLogger<AdminAuthFilter>.Instance, new AdminReplays(TimeProvider.System));

    private static string Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static async Task<object?> SendAsync(AdminAuthFilter filter, string method, string pathAndQuery, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var context = new DefaultHttpContext();
        var question = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        context.Request.Method = method;
        context.Request.Path = pathAndQuery[..question];
        context.Request.QueryString = new QueryString(pathAndQuery[question..]);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.Headers[AdminSignature.ActorHeader] = "2";
        context.Request.Headers[AdminSignature.SignatureHeader] = AdminSignature.Compute(Secret, method, pathAndQuery, "2", bytes);
        return await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(context), _ => ValueTask.FromResult<object?>("ran"));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
