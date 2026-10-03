using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>The /web/ endpoints of builds from 2008 to 2012 (b337 to b20121223, docs/old-clients.md).</summary>
public class EarlyWebEndpointsTests
{
    [Theory]
    [InlineData("/web/osu-getscores2.php", 2)]
    [InlineData("/web/osu-getscores3.php", 3)]
    [InlineData("/web/osu-getscores4.php", 4)]
    [InlineData("/web/osu-getscores5.php", 5)]
    [InlineData("/web/osu-getscores6.php", 6)]
    [InlineData("/web/osu-osz2-getscores.php", 0)]
    [InlineData("/web/osu-getscores7.php", 0)]
    public void TheLeaderboardsVersion_IsInItsPath(string path, int version) =>
        Assert.Equal(version, WebEndpoints.ScoresVersion(path));

    [Theory]
    // getscores2 (b337, b370) has no approved: "3" would be read as a score line.
    [InlineData(RankedStatus.Approved, 2, "2")]
    [InlineData(RankedStatus.Loved, 2, "2")]
    [InlineData(RankedStatus.Approved, 3, "3")]
    [InlineData(RankedStatus.Pending, 2, "0")]
    [InlineData(RankedStatus.UpdateAvailable, 4, "1")]
    [InlineData(RankedStatus.NotSubmitted, 5, "-1")]
    public void TheStatus_IsInTheVersionsNumbers(RankedStatus status, int version, string line) =>
        Assert.Equal(line, WebEndpoints.EarlyStatus(status, version));

    [Fact]
    public void TheOldestScoreLines_HaveTrueFalse_AndTheAvatarFile()
    {
        var score = new Score
        {
            Id = 9, UserId = 3, ScoreValue = 123456, MaxCombo = 100, Count300 = 90, Count100 = 5, Count50 = 1,
            CountMiss = 0, Perfect = true, Mods = 8, SubmittedAt = new DateTimeOffset(2009, 1, 2, 3, 4, 5, TimeSpan.Zero),
        };

        Assert.Equal("9|Sky|123456|100|1|5|90|0|0|0|True|8|3|3.png|2009-01-02 03:04:05", WebEndpoints.FormatOldestScoreLine(score, "Sky", showPp: false));
    }

    [Fact]
    public void TheTitle_IsOsusMarkup_OnTwoLines() =>
        Assert.Equal("[bold:0,size:20]Artist|Title", WebEndpoints.DisplayTitle(new Beatmap { Artist = "Artist", Title = "Title", Version = "[Hard]" }));

    [Fact]
    public void GetStatus_SaysRankedForMapsWithABoard()
    {
        var md5 = new string('a', 32);
        Assert.Equal(md5 + ",1,12,34,", WebEndpoints.StatusLine(md5, new Beatmap { Id = 12, SetId = 34, Status = RankedStatus.Loved }));
        Assert.Equal(md5 + ",0,12,34,", WebEndpoints.StatusLine(md5, new Beatmap { Id = 12, SetId = 34, Status = RankedStatus.Pending }));
    }

    [Theory]
    // A vote is answered with the average alone (one decimal at most: read in the player's number format).
    [InlineData((int)WebEndpoints.RateAnswer.Voted, 8.46, false, "8.5")]
    [InlineData((int)WebEndpoints.RateAnswer.Voted, 9.0, true, "9")]
    // ingame-rate2 shows the average after "alreadyvoted"; ingame-rate doesn't read it.
    [InlineData((int)WebEndpoints.RateAnswer.AlreadyVoted, 7.25, true, "alreadyvoted\n7.2")]
    [InlineData((int)WebEndpoints.RateAnswer.AlreadyVoted, 7.25, false, "alreadyvoted")]
    [InlineData((int)WebEndpoints.RateAnswer.Ok, 0, true, "ok")]
    [InlineData((int)WebEndpoints.RateAnswer.NotRanked, 0, true, "not ranked")]
    [InlineData((int)WebEndpoints.RateAnswer.AuthFail, 0, false, "auth fail")]
    public void OldRatings_AreAnsweredInTheirFormat(int answer, double average, bool withAverage, string reply) =>
        Assert.Equal(reply, WebEndpoints.OldRateReply((WebEndpoints.RateAnswer)answer, average, withAverage));

    [Theory]
    [InlineData("/web/osu-submit.php", true)]
    [InlineData("/web/osu-submit-new.php", true)]
    [InlineData("/web/osu-submit-modular.php", false)]
    [InlineData("/web/osu-submit-modular-selector.php", false)]
    public void ScoresFrom2008To2010_GetTheReplyInLines(string path, bool early) =>
        Assert.Equal(early, ScoreSubmissionEndpoint.IsEarlyPath(path));

    [Theory]
    [InlineData("123", 123, false)]
    [InlineData("123n", 123, true)]
    public void Downloads_TakeAnNForNoVideo(string value, int setId, bool noVideo) =>
        Assert.Equal((setId, noVideo), DirectEndpoints.ParseDownloadId(value));

    [Theory]
    [InlineData("abc")]
    [InlineData("123x")]
    [InlineData("n")]
    [InlineData("")]
    public void Downloads_OfSomethingElse_AreNotFound(string value) =>
        Assert.Null(DirectEndpoints.ParseDownloadId(value));

    [Theory]
    [InlineData("b1122", true)]
    [InlineData("b20120916", true)]
    [InlineData("b20121003shine.test", false)]
    [InlineData("b20140624", false)]
    [InlineData("", false)]
    public void Mania_CameWithB20121003(string version, bool withoutMania) =>
        Assert.Equal(withoutMania, !ClientBuild.Parse(version).HasMania);

    [Fact]
    public void OsuDirect_KnowsBuildsWithoutMania()
    {
        var registry = new PlayerRegistry();
        registry.Add(new BanchoPresence { UserId = 2, Username = "Old" }).Build = ClientBuild.Parse("b1700");
        registry.Add(new BanchoPresence { UserId = 3, Username = "New" }).Build = ClientBuild.Parse("b20140624");

        // The online player named by u, else the version sent.
        Assert.True(DirectEndpoints.HasNoMania(Request("u=old&h=x"), registry));
        Assert.False(DirectEndpoints.HasNoMania(Request("u=New&h=x"), registry));
        Assert.True(DirectEndpoints.HasNoMania(Request("v=b20120812"), registry));
        // Builds before b1652 send no player: over plain HTTP that's them; over HTTPS today's client.
        Assert.True(DirectEndpoints.HasNoMania(Request("r=4&q=Newest", plainHttp: true), registry));
        Assert.False(DirectEndpoints.HasNoMania(Request("r=4&q=Newest"), registry));
        // A player not online (or a malformed u) isn't assumed old.
        Assert.False(DirectEndpoints.HasNoMania(Request("u=someone&h=x", plainHttp: true), registry));
    }

    [Theory]
    [InlineData("[{\"Mode\":3},{\"Mode\":3}]", true)]
    [InlineData("[{\"Mode\":3},{\"Mode\":0}]", false)] // mixed sets stay listed
    [InlineData("[{\"Mode\":1}]", false)]
    [InlineData("[]", false)]
    public void ManiaOnlySets_AreTold(string children, bool maniaOnly)
    {
        using var set = JsonDocument.Parse("{\"SetID\":1,\"ChildrenBeatmaps\":" + children + "}");
        Assert.Equal(maniaOnly, DirectEndpoints.IsManiaOnly(set.RootElement));
    }

    [Theory]
    [InlineData("CN", "b20140624", "xx")]
    [InlineData("tw", "b20151228.3", "xx")]
    [InlineData("TH", "b20140624", "th")]
    [InlineData("cn", "b20240123", "cn")]
    public void BanchoConnect_NeverSendsOldBuildsToOsusChineseServer(string country, string version, string answer) =>
        Assert.Equal(answer, StableClientEndpoints.CountryAnswer(country, ClientBuild.Parse(version)));

    [Fact]
    public void Comments_StyleSupportersAsTheBuildExpects()
    {
        Assert.Equal("subscriber", StableClientEndpoints.CommentFormat(UserPrivileges.Normal | UserPrivileges.Supporter, oldClient: true));
        Assert.Equal("supporter", StableClientEndpoints.CommentFormat(UserPrivileges.Normal | UserPrivileges.Supporter));
        Assert.Equal("bat", StableClientEndpoints.CommentFormat(UserPrivileges.Administrator, oldClient: true));
    }

    [Fact]
    public void TheChatFilter_HasOneWord_AndNoEmptyLine()
    {
        Assert.DoesNotContain('\n', StableClientEndpoints.ChatFilterPlaceholder);
        Assert.NotEmpty(StableClientEndpoints.ChatFilterPlaceholder);
    }

    private static HttpRequest Request(string query, bool plainHttp = false)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?" + query);
        context.Request.Headers["Cf-Visitor"] = plainHttp ? "{\"scheme\":\"http\"}" : "{\"scheme\":\"https\"}";
        return context.Request;
    }
}
