using System.Text.Json;
using TomYum.Server.LastFm;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>Scrobbles Last.fm takes but drops (its "ignored" answer), and maps that aren't one song.</summary>
public class LastFmIgnoredTests
{
    [Fact]
    public void Dropped_GivesLastFmsReason()
    {
        using var answer = JsonDocument.Parse("""
            {"scrobbles":{"scrobble":{"artist":{"corrected":"0","#text":"Various Artists"},"ignoredMessage":{"code":"1","#text":"Artist name failed filter: Various Artists"}},"@attr":{"ignored":1,"accepted":0}}}
            """);

        Assert.Equal("Artist name failed filter: Various Artists", LastFmClient.IgnoredReason(answer.RootElement));
    }

    [Fact]
    public void Kept_IsNull()
    {
        using var answer = JsonDocument.Parse("""{"scrobbles":{"scrobble":{"ignoredMessage":{"code":"0","#text":""}},"@attr":{"ignored":0,"accepted":1}}}""");

        Assert.Null(LastFmClient.IgnoredReason(answer.RootElement));
    }

    [Theory]
    [InlineData("Various Artists", true)]
    [InlineData(" various artists ", true)]
    [InlineData("Zmey Gorynich", false)]
    public void NotASong(string artist, bool skipped) =>
        Assert.Equal(skipped, LastFmScrobbler.IsNotASong(new LastFmTrack(artist, "thing", 120)));
}
