using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using TomYum.Domain.Entities;
using TomYum.Domain.Services;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>osu-getreplay.php: the player's credentials when the client sends them, and restricted players' replays hidden.</summary>
public sealed class ReplayDownloadTests : IDisposable
{
    private const string Md5 = "5f4dcc3b5aa765d61d8327deb882cf99";

    private readonly string _dir = Directory.CreateTempSubdirectory("tomyum-replays").FullName;
    private readonly InMemoryUserRepository _users = new();
    private readonly InMemoryScoreRepository _scores = new();
    private readonly BcryptPasswordHasher _passwords = new();
    private readonly RestrictedUsers _restricted = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task A_public_replay_is_served_with_good_credentials()
    {
        var (owner, scoreId) = await SetUpAsync("alice");
        using var result = await AskAsync($"c={scoreId}&m=0&u={owner.Username}&h={Md5}") as ServedFile;
        Assert.IsType<FileStreamHttpResult>(result?.Result);
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        var (owner, scoreId) = await SetUpAsync("alice");
        Assert.IsType<UnauthorizedHttpResult>(await AskAsync($"c={scoreId}&m=0&u={owner.Username}&h=00000000000000000000000000000000"));
        Assert.IsType<UnauthorizedHttpResult>(await AskAsync($"c={scoreId}&m=0&u={owner.Username}"));
        Assert.IsType<UnauthorizedHttpResult>(await AskAsync($"c={scoreId}&m=0&u=nobody&h={Md5}"));
    }

    [Fact]
    public async Task Builds_without_credentials_get_public_replays_only()
    {
        var (_, publicScore) = await SetUpAsync("alice");
        using (var result = await AskAsync($"c={publicScore}") as ServedFile)
            Assert.IsType<FileStreamHttpResult>(result?.Result);

        var (restrictedOwner, hiddenScore) = await SetUpAsync("bob");
        _restricted.Set(restrictedOwner.Id, restricted: true);
        Assert.IsType<NotFound>(await AskAsync($"c={hiddenScore}"));
    }

    [Fact]
    public async Task A_restricted_players_replay_is_only_theirs_to_see()
    {
        var (owner, scoreId) = await SetUpAsync("bob");
        var (other, _) = await SetUpAsync("carol");
        _restricted.Set(owner.Id, restricted: true);

        Assert.IsType<NotFound>(await AskAsync($"c={scoreId}&m=0&u={other.Username}&h={Md5}"));
        using var result = await AskAsync($"c={scoreId}&m=0&u={owner.Username}&h={Md5}") as ServedFile;
        Assert.IsType<FileStreamHttpResult>(result?.Result);
    }

    [Fact]
    public async Task An_unknown_score_is_not_found() =>
        Assert.IsType<NotFound>(await AskAsync("c=999999"));

    private async Task<(User User, long ScoreId)> SetUpAsync(string name)
    {
        var user = await _users.AddAsync(new User
        {
            Username = name,
            SafeName = User.MakeSafeName(name),
            Email = name + "@example.com",
            PasswordBcrypt = _passwords.HashPassword(Md5),
        });
        var score = await _scores.AddAsync(new Score { UserId = user.Id, BeatmapMd5 = "map", Passed = true, Status = ScoreStatus.Best });
        await File.WriteAllBytesAsync(Path.Combine(_dir, $"{score.Id}.osr"), new byte[32]);
        return (user, score.Id);
    }

    private async Task<IResult> AskAsync(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?" + query);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Replays:Path"] = _dir }).Build();
        var result = await WebEndpoints.HandleGetReplayAsync(context, configuration, _scores, _users, _passwords, _restricted, CancellationToken.None);
        return result is FileStreamHttpResult file ? new ServedFile(file) : result;
    }

    /// <summary>A served file, closed after the test.</summary>
    private sealed class ServedFile(FileStreamHttpResult result) : IResult, IDisposable
    {
        public FileStreamHttpResult Result { get; } = result;

        public Task ExecuteAsync(HttpContext httpContext) => Result.ExecuteAsync(httpContext);

        public void Dispose() => Result.FileStream.Dispose();
    }
}
