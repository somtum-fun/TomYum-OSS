using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Services;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Admin;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Relax/Autopilot statistics in the website's format, for lazer's profiles in those modes.</summary>
public sealed class ModeStatsTests : OsuWebDatabaseTest
{
    [MySqlFact]
    public async Task RelaxStats_AreShapedLikeTheWebsites_WithRanks()
    {
        var db = await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var me = await users.AddAsync(new User { Username = "Me", SafeName = "me", Email = "m@x.dev", PasswordBcrypt = "x", Country = "nl" });
        var better = await users.AddAsync(new User { Username = "Better", SafeName = "better", Email = "b@x.dev", PasswordBcrypt = "x", Country = "de" });
        var countryman = await users.AddAsync(new User { Username = "Countryman", SafeName = "countryman", Email = "c@x.dev", PasswordBcrypt = "x", Country = "nl" });

        db.UserStats.AddRange(
            new UserStats { UserId = me.Id, Mode = 4, Pp = 100, Accuracy = 0.95f, PlayCount = 3, RankedScore = 5_000_000, TotalScore = 10_000_000 },
            new UserStats { UserId = better.Id, Mode = 4, Pp = 500, Accuracy = 0.99f, PlayCount = 9 },
            new UserStats { UserId = countryman.Id, Mode = 4, Pp = 50, Accuracy = 0.9f, PlayCount = 1 },
            new UserStats { UserId = me.Id, Mode = 0, Pp = 999 });
        db.Beatmaps.Add(new Beatmap { Id = 1, SetId = 1, Md5 = "m1", TotalLength = 120, Artist = "A", Title = "T", Version = "V", Creator = "C" });
        db.Scores.AddRange(
            new Score { UserId = me.Id, BeatmapMd5 = "m1", Mode = 4, Grade = "S", Status = ScoreStatus.Best, Passed = true, Count300 = 100, Count100 = 5, CountMiss = 1, MaxCombo = 300 },
            new Score { UserId = me.Id, BeatmapMd5 = "m1", Mode = 4, Grade = "A", Status = ScoreStatus.Submitted, Passed = true, Count300 = 90, Count100 = 10, MaxCombo = 450 },
            new Score { UserId = me.Id, BeatmapMd5 = "m1", Mode = 0, Grade = "X", Status = ScoreStatus.Best, Passed = true, Count300 = 999, MaxCombo = 999 });
        await db.SaveChangesAsync();

        var result = await AdminModeStatsEndpoints.GetAsync(me.Id, 4, users, NewTomYumContext(), new RestrictedUsers(), CancellationToken.None);
        var json = await Body(result);

        Assert.Equal((2, 1), (json.GetProperty("global_rank").GetInt32(), json.GetProperty("country_rank").GetInt32()));   // behind Better; first in nl
        Assert.Equal(100, json.GetProperty("pp").GetDouble());
        Assert.Equal(95, json.GetProperty("hit_accuracy").GetDouble(), 3);
        Assert.Equal(0.95, json.GetProperty("accuracy").GetDouble(), 3);
        Assert.Equal((190, 15, 1, 450), (json.GetProperty("count_300").GetInt32(), json.GetProperty("count_100").GetInt32(), json.GetProperty("count_miss").GetInt32(), json.GetProperty("maximum_combo").GetInt32()));
        Assert.Equal((1, 0), (json.GetProperty("grade_counts").GetProperty("s").GetInt32(), json.GetProperty("grade_counts").GetProperty("a").GetInt32()));   // best scores only
        Assert.Equal(240, json.GetProperty("play_time").GetInt64());
        Assert.True(json.GetProperty("level").GetProperty("current").GetInt32() > 1);
        Assert.True(json.GetProperty("is_ranked").GetBoolean());

        var vanilla = await AdminModeStatsEndpoints.GetAsync(me.Id, 0, users, NewTomYumContext(), new RestrictedUsers(), CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(vanilla).StatusCode);
    }

    private static async Task<JsonElement> Body(IResult result)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (await JsonDocument.ParseAsync(context.Response.Body)).RootElement;
    }
}
