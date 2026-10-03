using Microsoft.Extensions.Configuration;

using TomYum.Server.Bancho;

using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// Tests how <see cref="TomYumVersion"/> splits the assembly informational version into a display
/// version and the baked git commit (the <c>+&lt;SourceRevisionId&gt;</c> suffix the SDK appends).
/// </summary>
public class TomYumVersionTests
{
    [Fact]
    public void Splits_version_and_commit_at_the_plus()
    {
        var (display, commit) = TomYumVersion.ParseInformationalVersion("0.1.0-dev+cf33d8fee605");
        Assert.Equal("0.1.0-dev", display);
        Assert.Equal("cf33d8fee605", commit);
    }

    [Fact]
    public void Commit_is_unknown_when_no_revision_is_baked_in()
    {
        var (display, commit) = TomYumVersion.ParseInformationalVersion("0.1.0-dev");
        Assert.Equal("0.1.0-dev", display);
        Assert.Equal(TomYumVersion.UnknownCommit, commit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Falls_back_when_the_informational_version_is_missing(string? informational)
    {
        var (display, commit) = TomYumVersion.ParseInformationalVersion(informational);
        Assert.Equal(TomYumVersion.Fallback, display);
        Assert.Equal(TomYumVersion.UnknownCommit, commit);
    }

    [Fact]
    public void Empty_commit_suffix_falls_back_to_unknown()
    {
        var (_, commit) = TomYumVersion.ParseInformationalVersion("0.1.0-dev+");
        Assert.Equal(TomYumVersion.UnknownCommit, commit);
    }

    [Fact]
    public void Live_version_accessor_exposes_a_display_and_commit()
    {
        var version = new TomYumVersion();
        Assert.False(string.IsNullOrWhiteSpace(version.Display));
        Assert.False(string.IsNullOrWhiteSpace(version.Commit)); // a sha or "unknown", never blank
    }

    [Fact]
    public void Server_name_comes_from_configuration_and_leads_the_label()
    {
        var named = new TomYumVersion(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Server:Name"] = " example " }).Build());
        Assert.Equal("example", named.ServerName);
        Assert.Equal($"example · TomYum {named.Display}", named.Label);

        var unnamed = new TomYumVersion();
        Assert.Equal("TomYum", unnamed.ServerName);
        Assert.Equal($"TomYum {unnamed.Display}", unnamed.Label);
    }
}
