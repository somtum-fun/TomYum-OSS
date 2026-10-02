using TomYum.Bancho;
using Xunit;

namespace TomYum.Bancho.Tests;

/// <summary>
/// Tests for the plaintext login-body parser and the login response burst, against the
/// format in <c>memories/protocols/bancho-stable.md</c> §3.
/// </summary>
public class LoginTests
{
    private const string ValidBody =
        "TomYum\n" +
        "deadbeefdeadbeefdeadbeefdeadbeef\n" +
        "b20231228.1|8|1|abc:runningunderwine:def:ghi:jkl:|0\n";

    [Fact]
    public void TryParse_ParsesValidBody()
    {
        Assert.True(LoginRequest.TryParse(ValidBody, out var request));
        Assert.NotNull(request);
        Assert.Equal("TomYum", request!.Username);
        Assert.Equal("deadbeefdeadbeefdeadbeefdeadbeef", request.PasswordMd5);
        Assert.Equal("b20231228.1", request.OsuVersion);
        Assert.Equal(8, request.UtcOffset);
        Assert.True(request.DisplayCity);
        Assert.False(request.PmPrivate);
    }

    [Fact]
    public void TryParse_StripsTrailingColonFromClientHashes()
    {
        Assert.True(LoginRequest.TryParse(ValidBody, out var request));
        Assert.Equal("abc", request!.ClientHashes.OsuPathMd5);
        Assert.Equal("runningunderwine", request.ClientHashes.AdaptersStr);
        Assert.Equal("jkl", request.ClientHashes.DiskSignatureMd5);
    }

    [Theory]
    [InlineData("")]
    [InlineData("onlyoneline")]
    [InlineData("user\npass\n")] // third line not pipe-delimited
    [InlineData("user\npass\nb1|notanumber|1|a:b:c:d:e:|0\n")] // bad utc offset
    [InlineData("user\npass\nb1\n")] // only osu_version, no utc_offset
    public void TryParse_RejectsMalformedBody(string body)
    {
        Assert.False(LoginRequest.TryParse(body, out var request));
        Assert.Null(request);
    }

    [Fact]
    public void TryParse_AcceptsOldStableWithoutPmPrivate()
    {
        // b20121030 and earlier predate the pm_private field; they send only 4 pipe fields.
        const string body = "TomYum\n"
                          + "deadbeefdeadbeefdeadbeefdeadbeef\n"
                          + "b20121030|7|1|abc:runningunderwine:def:ghi:jkl:\n";

        Assert.True(LoginRequest.TryParse(body, out var request));
        Assert.NotNull(request);
        Assert.Equal("b20121030", request!.OsuVersion);
        Assert.Equal(7, request.UtcOffset);
        Assert.True(request.DisplayCity);
        Assert.False(request.PmPrivate); // default when the field isn't sent
        Assert.Equal("abc", request.ClientHashes.OsuPathMd5);
        Assert.Equal("jkl", request.ClientHashes.DiskSignatureMd5);
    }

    [Fact]
    public void TryParse_AcceptsPreHashOldStableWithOnlyVersionAndOffset()
    {
        // Pre-b1661 stable sends no client_hashes at all. Blank hashes land in the parsed request;
        // the server's hardware-match treats them as unusable (no false multi-account matches).
        const string body = "TomYum\n"
                          + "deadbeefdeadbeefdeadbeefdeadbeef\n"
                          + "b815|7\n";

        Assert.True(LoginRequest.TryParse(body, out var request));
        Assert.NotNull(request);
        Assert.Equal("b815", request!.OsuVersion);
        Assert.Equal(7, request.UtcOffset);
        Assert.False(request.DisplayCity);
        Assert.False(request.PmPrivate);
        Assert.Equal(string.Empty, request.ClientHashes.OsuPathMd5);
        Assert.Equal(string.Empty, request.ClientHashes.AdaptersMd5);
    }

    [Fact]
    public void BuildFailure_IsSingleUserIdPacketWithNegativeCode()
    {
        var bytes = LoginResponseBuilder.BuildFailure(LoginFailureReason.AuthenticationFailed);

        var reader = new BanchoPacketReader(bytes);
        var header = reader.ReadHeader();
        Assert.Equal(ServerPacketId.UserId, (ServerPacketId)header.Id);
        Assert.Equal(-1, reader.ReadInt32());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void BuildSuccess_StartsWithProtocolVersionThenUserId()
    {
        var presence = new BanchoPresence { UserId = 2, Username = "TomYum" };

        var bytes = LoginResponseBuilder.BuildSuccess(presence, "Welcome!");
        var reader = new BanchoPacketReader(bytes);

        // 1. PROTOCOL_VERSION = 19
        var first = reader.ReadHeader();
        Assert.Equal(ServerPacketId.ProtocolVersion, (ServerPacketId)first.Id);
        Assert.Equal(LoginResponseBuilder.ProtocolVersion, reader.ReadInt32());

        // 2. USER_ID = the user's id (success).
        var second = reader.ReadHeader();
        Assert.Equal(ServerPacketId.UserId, (ServerPacketId)second.Id);
        Assert.Equal(2, reader.ReadInt32());
    }
}
