using TomYum.Bancho;
using Xunit;

namespace TomYum.Bancho.Tests;

/// <summary>The osu! build from a login's version string (docs/old-clients.md).</summary>
public class ClientBuildTests
{
    [Theory]
    [InlineData("b20140624", 2014, 6, 24, true)]
    [InlineData("b20150101cuttingedge", 2015, 1, 1, true)]
    [InlineData("b20151231", 2015, 12, 31, true)]
    [InlineData("b20160101", 2016, 1, 1, false)]
    [InlineData("b20240123.2", 2024, 1, 23, false)]
    [InlineData("b20240123tourney", 2024, 1, 23, false)]
    public void TheDate_IsReadFromTheVersion(string version, int year, int month, int day, bool legacy)
    {
        var build = ClientBuild.Parse(version);

        Assert.Equal(new DateOnly(year, month, day), build.Date);
        Assert.Equal(legacy, build.IsLegacy);
        Assert.Equal(version, build.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("stable40")]
    [InlineData("b2014")]
    [InlineData("b20141399")]
    public void WithoutADate_ItCountsAsTodaysClient(string? version)
    {
        var build = ClientBuild.Parse(version);

        Assert.Null(build.Date);
        Assert.False(build.IsLegacy);
    }
}
