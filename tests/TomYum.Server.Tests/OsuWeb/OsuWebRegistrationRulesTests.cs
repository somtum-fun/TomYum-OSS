using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Server.Bancho;
using TomYum.Server.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>
/// Stable's in-game sign-up asking osu-web's sign-up rules (<c>/_lio/tomyum/registration-check</c>),
/// and the sign-up address kept on the account.
/// </summary>
public class OsuWebRegistrationRulesTests
{
    [Fact]
    public async Task CheckRegistration_AllowedSignUp_ReturnsNull()
    {
        var handler = new JsonHandler(HttpStatusCode.OK, """{"ok":true}""");
        var interop = NewInterop(handler);

        var blocked = await interop.CheckRegistrationAsync("203.0.113.7", "New Player", "TH", quick: false);

        Assert.Null(blocked);
        Assert.StartsWith("/_lio/tomyum/registration-check?timestamp=", handler.Request!.RequestUri!.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("\"ip\":\"203.0.113.7\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"check\":false", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"stable\"", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRegistration_RefusedSignUp_ReturnsTheWebsitesMessage()
    {
        var interop = NewInterop(new JsonHandler(HttpStatusCode.OK,
            """{"ok":false,"code":"blocked_proxy","message":"You can't make an account through a VPN, proxy or hosting network."}"""));

        var blocked = await interop.CheckRegistrationAsync("203.0.113.7", "New Player", null, quick: true);

        Assert.Equal("You can't make an account through a VPN, proxy or hosting network.", blocked);
    }

    [Fact]
    public async Task CheckRegistration_StockOsuWeb_AllowsIt()
    {
        var interop = NewInterop(new JsonHandler(HttpStatusCode.NotFound, "{}"));

        Assert.Null(await interop.CheckRegistrationAsync("203.0.113.7", "New Player", null, quick: false));
    }

    [Fact]
    public async Task CheckRegistration_OtherFailuresThrow()
    {
        var interop = NewInterop(new JsonHandler(HttpStatusCode.InternalServerError, "{}"));

        await Assert.ThrowsAsync<HttpRequestException>(() => interop.CheckRegistrationAsync("203.0.113.7", "New Player", null, quick: false));
    }

    [Fact]
    public async Task RegistrationCreated_SendsTheAccountAndAddress()
    {
        var handler = new JsonHandler(HttpStatusCode.NoContent, string.Empty);

        await NewInterop(handler).RegistrationCreatedAsync(12, "New Player", "2001:db8::1", "TH");

        Assert.StartsWith("/_lio/tomyum/registration-created?timestamp=", handler.Request!.RequestUri!.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("\"user_id\":12", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"ip\":\"2001:db8::1\"", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoOsuWebSync_AllowsEverySignUp()
    {
        var sync = new NoOsuWebSync();

        Assert.Null(await sync.CheckRegistrationAsync("203.0.113.7", "New Player", null, quick: false));
        await sync.RegistrationCreatedAsync(12, "New Player", "203.0.113.7", null);
    }

    [Fact]
    public async Task Register_KeepsTheSignUpAddress()
    {
        var service = new RegistrationService(new InMemoryUserRepository(), new BcryptPasswordHasher(), NullLogger<RegistrationService>.Instance);
        // (The first account becomes the owner, a copy made after the insert.)
        await service.RegisterAsync("Owner", "owner@sky.dev", "s3cure-pass", create: true, country: "th", CancellationToken.None);

        var result = await service.RegisterAsync("BlueSky", "blue@sky.dev", "s3cure-pass", create: true, country: "th", CancellationToken.None, "203.0.113.7");

        Assert.True(result.IsValid);
        Assert.Equal("203.0.113.7", result.Created?.RegistrationIp);
    }

    private static OsuWebInterop NewInterop(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new OsuWebInteropOptions { InteropUrl = "http://nginx", InteropSecret = "s" });

    private sealed class JsonHandler(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
