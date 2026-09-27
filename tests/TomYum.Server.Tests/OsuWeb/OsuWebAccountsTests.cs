using TomYum.Domain.Enums;
using TomYum.Persistence.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Translation rules between TomYum accounts and osu-web's (no database needed).</summary>
public class OsuWebAccountsTests
{
    [Fact]
    public void ToPrivileges_PlainMember_IsNormal()
    {
        Assert.Equal(UserPrivileges.Normal, OsuWebAccounts.ToPrivileges(["default"], osuSubscriber: false));
    }

    [Fact]
    public void ToPrivileges_AllStaffGroupsPlusSupporter_IsOwner()
    {
        var privileges = OsuWebAccounts.ToPrivileges(["default", "admin", "dev", "gmt"], osuSubscriber: true);

        Assert.Equal(UserPrivileges.Owner, privileges);
    }

    [Fact]
    public void ToPrivileges_IgnoresGroupsTomYumDoesNotManage()
    {
        var privileges = OsuWebAccounts.ToPrivileges(["default", "nat", "bng", null], osuSubscriber: false);

        Assert.Equal(UserPrivileges.Normal, privileges);
    }

    [Theory]
    [InlineData(UserPrivileges.Owner, new[] { "admin", "dev", "gmt" })]
    [InlineData(UserPrivileges.Normal | UserPrivileges.Moderator, new[] { "gmt" })]
    [InlineData(UserPrivileges.Normal | UserPrivileges.Supporter, new string[0])]
    public void StaffGroupsFor_MapsEachStaffFlag(UserPrivileges privileges, string[] expected)
    {
        Assert.Equal(expected.Order(), OsuWebAccounts.StaffGroupsFor(privileges).Order());
    }

    [Theory]
    [InlineData("Some Player", "some player")] // spaces kept, unlike TomYum's safe name
    [InlineData("ABC_xyz", "abc_xyz")]
    [InlineData("Ünïcode", "Ünïcode")]         // PHP strtolower only touches ASCII
    public void CleanUsername_MatchesPhpStrtolower(string username, string expected)
    {
        Assert.Equal(expected, OsuWebAccounts.CleanUsername(username));
    }

    [Fact]
    public void CleanNameCandidates_TriesUnderscoresAsSpaces()
    {
        Assert.Equal(["some_player", "some player"], OsuWebAccounts.CleanNameCandidates("some_player"));
        Assert.Equal(["plain"], OsuWebAccounts.CleanNameCandidates("plain"));
    }

    [Theory]
    [InlineData("th", "TH")]
    [InlineData("", "XX")]
    public void Country_IsUpperCaseInOsuWeb(string tomyum, string osuWeb)
    {
        Assert.Equal(osuWeb, OsuWebAccounts.ToOsuWebCountry(tomyum));
        Assert.Equal(tomyum.Length == 0 ? "xx" : tomyum, OsuWebAccounts.FromOsuWebCountry(osuWeb));
    }
}
