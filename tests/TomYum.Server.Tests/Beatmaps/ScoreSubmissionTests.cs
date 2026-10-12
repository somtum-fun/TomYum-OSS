using TomYum.Bancho;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>What stable score submission accepts (ScoreSubmissionEndpoint).</summary>
public class ScoreSubmissionTests
{
    [Theory]
    [InlineData("b20240123.2", false, true)] // today's client, today's URL
    [InlineData("b20240123.2", true, false)] // today's client posting as an old one (to skip today's checks)
    [InlineData("", true, false)] // a session without a build is today's client
    [InlineData("b20140624", true, true)]
    [InlineData("b20151228.3", true, true)]
    [InlineData("b20140624", false, false)]
    [InlineData("b1122", true, true)]
    [InlineData("b337", true, true)]
    public void The_session_build_decides_old_or_todays(string version, bool legacyPath, bool accepted) =>
        Assert.Equal(accepted, ScoreSubmissionEndpoint.SessionMatchesPath(ClientBuild.Parse(version), legacyPath));
}
