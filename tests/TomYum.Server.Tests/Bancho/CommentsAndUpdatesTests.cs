using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Bancho;

/// <summary>osu!stable's in-game comments (<c>osu-comment.php</c>) and its status update filter (RECEIVE_UPDATES).</summary>
public sealed class CommentsAndUpdatesTests
{
    private readonly InMemoryUserRepository _users = new();
    private readonly InMemoryBeatmapCommentRepository _comments = new();

    [Fact]
    public async Task Comments_OnTheSongMapAndReplay_ShowWhilePlaying()
    {
        await AddUserAsync("Player");
        await AddUserAsync("Fan", UserPrivileges.Normal | UserPrivileges.Supporter);
        await AddUserAsync("Staff", UserPrivileges.Normal | UserPrivileges.Moderator);

        Assert.Equal(string.Empty, await Post("Player", "song", 1500, "nice drop", colour: "ff0000"));  // not a supporter: no colour
        await Post("Fan", "map", 500, "here we go", colour: "FF66AA");
        await Post("Staff", "replay", 900, "watch this");

        Assert.Equal(["500\tmap\tsupporter|ff66aa\there we go", "1500\tsong\t\tnice drop"], (await Get(scoreId: 0)).Split('\n'));
        Assert.Equal(["500\tmap\tsupporter|ff66aa\there we go", "900\treplay\tbat\twatch this", "1500\tsong\t\tnice drop"], (await Get(scoreId: 77)).Split('\n'));
    }

    [Fact]
    public async Task Comments_NeedTheRightPassword_AndSilencedPlayersCantPost()
    {
        await AddUserAsync("Player");
        await AddUserAsync("Quiet", silenced: true);

        Assert.IsType<UnauthorizedHttpResult>(await CallAsync(Form("Player", "a", "post", "target", "song", "starttime", "1", "comment", "hi", password: "wrong")));
        await Post("Quiet", "song", 100, "can I talk?");
        Assert.Equal(string.Empty, await Get(scoreId: 0));
    }

    [Fact]
    public void StatusUpdates_FollowEachPlayersFilter()
    {
        var registry = new PlayerRegistry();
        var subject = registry.Add(new BanchoPresence { UserId = 1, Username = "Subject" });
        var everyone = registry.Add(new BanchoPresence { UserId = 2, Username = "Everyone" });
        var friendsOnly = registry.Add(new BanchoPresence { UserId = 3, Username = "FriendsOnly" });
        var friendOfSubject = registry.Add(new BanchoPresence { UserId = 4, Username = "Friend" });
        var nobody = registry.Add(new BanchoPresence { UserId = 5, Username = "Nobody" });
        friendsOnly.PresenceFilter = PresenceFilter.Friends;
        friendOfSubject.PresenceFilter = PresenceFilter.Friends;
        friendOfSubject.Friends = [subject.UserId];
        nobody.PresenceFilter = PresenceFilter.None;
        byte[] packet = [1, 2, 3];

        registry.EnqueueAbout(subject, packet, exceptUserId: subject.UserId);
        Assert.Equal((3, 0, 3, 0), (everyone.DequeueAll().Length, friendsOnly.DequeueAll().Length, friendOfSubject.DequeueAll().Length, nobody.DequeueAll().Length));

        // Logging in is announced to everyone, so user lists stay right.
        registry.EnqueueAbout(subject, packet, exceptUserId: subject.UserId, isUpdate: false);
        Assert.Equal((3, 3, 3, 3), (everyone.DequeueAll().Length, friendsOnly.DequeueAll().Length, friendOfSubject.DequeueAll().Length, nobody.DequeueAll().Length));
    }

    [Theory]
    [InlineData(0, PresenceFilter.None)]
    [InlineData(1, PresenceFilter.All)]
    [InlineData(2, PresenceFilter.Friends)]
    [InlineData(9, PresenceFilter.All)]
    public async Task ReceiveUpdates_SetsTheFilter(int value, PresenceFilter expected)
    {
        var registry = new PlayerRegistry();
        var player = registry.Add(new BanchoPresence { UserId = 1, Username = "P" });
        player.PresenceFilter = PresenceFilter.Friends;

        await new ReceiveUpdatesHandler().HandleAsync(new ClientPacketContext(player, registry, new ChannelRegistry(), new MatchRegistry(),
            new ServiceCollection().BuildServiceProvider(), new BanchoPacketWriter(4).WriteInt32(value).ToArray(), new BanchoPacketWriter(8)));

        Assert.Equal(expected, player.PresenceFilter);
    }

    private async Task<string> Post(string user, string target, int time, string text, string colour = "") =>
        Text(await CallAsync(Form(user, "a", "post", "target", target, "starttime", time.ToString(System.Globalization.CultureInfo.InvariantCulture), "comment", text, "f", colour, "r", "77")));

    private async Task<string> Get(long scoreId) =>
        Text(await CallAsync(Form("Player", "a", "get", "r", scoreId.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    private async Task<IResult> CallAsync(string form)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IUserRepository>(_users)
                .AddSingleton<IPasswordHasher, BcryptPasswordHasher>()
                .AddSingleton<IBeatmapCommentRepository>(_comments)
                .BuildServiceProvider(),
        };
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(form));
        return await StableClientEndpoints.HandleCommentAsync(context, CancellationToken.None);
    }

    private static string Form(string user, params string[] fields) => Form(user, fields, "password");

    private static string Form(string user, string a, string b, string c, string d, string e, string f, string g, string h, string password) =>
        Form(user, [a, b, c, d, e, f, g, h], password);

    private static string Form(string user, string[] fields, string password)
    {
        var all = new List<string> { "u", user, "p", new BcryptPasswordHasher().ComputeMd5Hex(password), "b", "20", "s", "10" };
        all.AddRange(fields);
        return string.Join('&', Enumerable.Range(0, all.Count / 2).Select(i => $"{all[2 * i]}={Uri.EscapeDataString(all[(2 * i) + 1])}"));
    }

    private static string Text(IResult result) => Assert.IsType<ContentHttpResult>(result).ResponseContent ?? string.Empty;

    private async Task AddUserAsync(string name, UserPrivileges privileges = UserPrivileges.Normal, bool silenced = false)
    {
        var hasher = new BcryptPasswordHasher();
        await _users.AddAsync(new User
        {
            Username = name,
            SafeName = User.MakeSafeName(name),
            Email = $"{name.ToLowerInvariant()}@tomyum.dev",
            PasswordBcrypt = hasher.HashPassword(hasher.ComputeMd5Hex("password")),
            Privileges = privileges,
            SilenceEndUtc = silenced ? DateTimeOffset.UtcNow.AddHours(1) : null,
        });
    }
}
