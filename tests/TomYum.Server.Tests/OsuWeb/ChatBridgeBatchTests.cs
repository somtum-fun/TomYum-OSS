using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Server.Bancho;
using TomYum.Server.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Game chat goes to the website in batches (<c>/_lio/tomyum/chat-messages</c>), one by one on an older website.</summary>
public sealed class ChatBridgeBatchTests
{
    private sealed class Website(bool hasBatch) : HttpMessageHandler
    {
        public List<(string Path, JsonElement Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            lock (Requests)
                Requests.Add((request.RequestUri!.AbsolutePath, body));
            return request.RequestUri.AbsolutePath.EndsWith("chat-messages", StringComparison.Ordinal) && !hasBatch
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private static async Task<(OsuWebChatBridge Bridge, Website Site, int FriendId)> NewAsync(bool hasBatch)
    {
        var users = new InMemoryUserRepository();
        var friend = await users.AddAsync(new User { Username = "Friend", SafeName = "friend", Email = "f@example.com", PasswordBcrypt = "x", Country = "th" });
        var site = new Website(hasBatch);
        var services = new ServiceCollection()
            .AddSingleton<IUserRepository>(users)
            .AddSingleton(new OsuWebInterop(new HttpClient(site), new OsuWebInteropOptions { InteropUrl = "http://web/", InteropSecret = "s" }))
            .BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var bridge = new OsuWebChatBridge(scopes, new PlayerRegistry(), new ChannelRegistry(), new ChatLog(scopes, TimeProvider.System, NullLogger<ChatLog>.Instance),
            NullLogger<OsuWebChatBridge>.Instance);
        return (bridge, site, friend.Id);
    }

    [Fact]
    public async Task SeveralMessages_GoInOneRequest()
    {
        var (bridge, site, friendId) = await NewAsync(hasBatch: true);
        await bridge.SendBatchToWebsiteAsync([new(5, "#osu", null, "one"), new(5, "#osu", null, "\u0001ACTION waves\u0001"), new(5, null, "Friend", "hi")], CancellationToken.None);

        var (path, body) = Assert.Single(site.Requests);
        Assert.Equal("/_lio/tomyum/chat-messages", path);
        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["one", "waves", "hi"], messages.Select(m => m.GetProperty("message").GetString()));
        Assert.True(messages[1].GetProperty("is_action").GetBoolean());
        Assert.Equal(friendId, messages[2].GetProperty("target_id").GetInt32());
    }

    [Fact]
    public async Task AnOlderWebsite_GetsThemOneByOne_FromThenOn()
    {
        var (bridge, site, _) = await NewAsync(hasBatch: false);
        await bridge.SendBatchToWebsiteAsync([new(5, "#osu", null, "a"), new(5, "#osu", null, "b")], CancellationToken.None);
        await bridge.SendBatchToWebsiteAsync([new(5, "#osu", null, "c"), new(5, "#osu", null, "d")], CancellationToken.None);

        Assert.Equal(["/_lio/tomyum/chat-messages", "/_lio/tomyum/chat-message", "/_lio/tomyum/chat-message", "/_lio/tomyum/chat-message", "/_lio/tomyum/chat-message"],
            site.Requests.Select(r => r.Path));
        Assert.Equal(["a", "b", "c", "d"], site.Requests.Skip(1).Select(r => r.Body.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task APmToNobody_IsLeftOut()
    {
        var (bridge, site, _) = await NewAsync(hasBatch: true);
        await bridge.SendBatchToWebsiteAsync([new(5, null, "Nobody", "hello?"), new(5, "#osu", null, "still here")], CancellationToken.None);

        var body = Assert.Single(site.Requests).Body;
        Assert.Equal(["still here"], body.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task MessagesPastTheQueue_AreCounted()
    {
        var (bridge, _, _) = await NewAsync(hasBatch: true);
        for (var i = 0; i < 5_010; i++)
            bridge.ForwardChannelMessage(5, "#osu", "spam " + i);

        Assert.Equal(10, bridge.Dropped);
    }
}
