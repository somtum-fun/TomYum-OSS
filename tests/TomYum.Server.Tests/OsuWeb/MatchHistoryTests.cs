using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Match history in osu-web's osu_mp tables (the website's /community/matches pages).</summary>
public sealed class MatchHistoryTests : OsuWebDatabaseTest
{
    // osu-web's osu_mp tables, as the website's database has them.
    private const string MpSchema = """
        CREATE TABLE matches (match_id int unsigned NOT NULL AUTO_INCREMENT, start_time timestamp NULL DEFAULT CURRENT_TIMESTAMP,
          end_time timestamp NULL DEFAULT NULL, name varchar(255) NOT NULL, private bit(1) NOT NULL,
          keep_forever bit(1) NOT NULL DEFAULT b'0', PRIMARY KEY (match_id));
        CREATE TABLE events (event_id int unsigned NOT NULL AUTO_INCREMENT, match_id int unsigned NOT NULL,
          game_id int unsigned DEFAULT NULL, user_id int unsigned DEFAULT NULL, text varchar(255) DEFAULT NULL,
          timestamp timestamp NULL DEFAULT CURRENT_TIMESTAMP, PRIMARY KEY (event_id));
        CREATE TABLE games (game_id int unsigned NOT NULL AUTO_INCREMENT, match_id int unsigned DEFAULT NULL,
          start_time timestamp NULL DEFAULT CURRENT_TIMESTAMP, end_time timestamp NULL DEFAULT NULL,
          beatmap_id mediumint unsigned DEFAULT NULL, play_mode tinyint unsigned DEFAULT NULL, match_type tinyint unsigned DEFAULT NULL,
          scoring_type tinyint unsigned DEFAULT NULL, team_type tinyint unsigned DEFAULT NULL, mods bigint unsigned DEFAULT NULL,
          PRIMARY KEY (game_id));
        CREATE TABLE game_scores (game_id int unsigned NOT NULL, slot tinyint unsigned NOT NULL, team tinyint unsigned NOT NULL DEFAULT '0',
          user_id int unsigned NOT NULL, score int NOT NULL DEFAULT '0', maxcombo smallint unsigned NOT NULL DEFAULT '0',
          `rank` enum('0','A','B','C','D','S','SH','X','XH') NOT NULL DEFAULT '0', count50 smallint unsigned NOT NULL DEFAULT '0',
          count100 smallint unsigned NOT NULL DEFAULT '0', count300 smallint unsigned NOT NULL DEFAULT '0',
          countmiss smallint unsigned NOT NULL DEFAULT '0', countgeki smallint unsigned NOT NULL DEFAULT '0',
          countkatu smallint unsigned NOT NULL DEFAULT '0', perfect tinyint NOT NULL DEFAULT '0', pass tinyint NOT NULL DEFAULT '0',
          frame bigint unsigned NOT NULL DEFAULT '0', enabled_mods int DEFAULT NULL, PRIMARY KEY (game_id, slot));
        """;

    [MySqlFact]
    public async Task RecordsARoomItsPlayersAndAGameWithScores()
    {
        await ExecuteAsync(MpSchema);
        using var history = new MatchHistory(OsuWebConnectionString, "https://osu.example.com", new PlayerRegistry(), NullLogger<MatchHistory>.Instance);
        await history.StartAsync(CancellationToken.None);

        var matches = new MatchRegistry { History = history };
        var match = matches.Create(10, "peppy's game", "secret", "Some map", 75, "md5", 0,
            MatchWinCondition.Score, MatchTeamType.TeamVs, freeMods: true, mods: 0, seed: 0);
        match.Slots[0].Join(10);
        match.Slots[0].Team = 1;
        match.History!.Created(match);
        match.Slots[1].Join(20);
        match.Slots[1].Team = 2;
        match.Slots[1].Mods = 8; // HD, free mods
        match.History.Joined(match, 20);

        match.Slots[0].Status = SlotStatus.Playing;
        match.Slots[1].Status = SlotStatus.Playing;
        match.History.GameStarted(match);
        match.History.ScoreFrame(match, 0, Frame(n300: 100, n100: 2, miss: 0, score: 123456, maxCombo: 150, perfect: true));
        match.History.ScoreFrame(match, 1, Frame(n300: 90, n100: 10, miss: 3, score: 65432, maxCombo: 60, perfect: false));
        match.History.Failed(match, 1);
        match.History.GameEnded(match);
        match.History.HostChanged(match, 20);
        match.History.Left(match, 10);
        match.History.Disbanded(match);

        await WaitForAsync("SELECT COUNT(*) FROM matches WHERE end_time IS NOT NULL", 1);
        await history.StopAsync(CancellationToken.None);

        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM matches WHERE name = 'peppy''s game' AND private = 1"));
        Assert.Equal("CREATE,JOIN,(game),HOST,PART,DISBAND",
            await ScalarAsync<string>("SELECT GROUP_CONCAT(IFNULL(text, '(game)') ORDER BY event_id) FROM events"));
        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM games WHERE beatmap_id = 75 AND team_type = 2 AND scoring_type = 0 AND end_time IS NOT NULL"));
        Assert.Equal("10:1:123456:150:100:2:1:1:0,20:2:65432:60:90:10:0:0:8", await ScalarAsync<string>(
            "SELECT GROUP_CONCAT(CONCAT_WS(':', user_id, team, score, maxcombo, count300, count100, perfect, pass, IFNULL(enabled_mods, 'null')) " +
            "ORDER BY slot) FROM game_scores"));
    }

    [MySqlFact]
    public async Task AnAbortedGameHasNoScoresAndRoomsLeftOpenAreClosedAtStart()
    {
        await ExecuteAsync(MpSchema);
        await ExecuteAsync("INSERT INTO matches (name, private) VALUES ('left over', 0)");
        using var history = new MatchHistory(OsuWebConnectionString, "https://osu.example.com", new PlayerRegistry(), NullLogger<MatchHistory>.Instance);
        await history.StartAsync(CancellationToken.None);

        var match = new MatchRegistry { History = history }.Create(10, "room", "", "map", 75, "md5", 0,
            MatchWinCondition.Score, MatchTeamType.HeadToHead, freeMods: false, mods: 64, seed: 0);
        match.Slots[0].Join(10);
        match.History!.Created(match);
        match.Slots[0].Status = SlotStatus.Playing;
        match.History.GameStarted(match);
        match.History.ScoreFrame(match, 0, Frame(n300: 10, n100: 0, miss: 0, score: 1000, maxCombo: 10, perfect: true));
        match.History.GameAborted(match);
        match.History.Disbanded(match);

        await WaitForAsync("SELECT COUNT(*) FROM matches WHERE end_time IS NOT NULL", 2);
        await history.StopAsync(CancellationToken.None);

        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM games WHERE mods = 64 AND end_time IS NOT NULL"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM game_scores"));
    }

    private static byte[] Frame(ushort n300, ushort n100, ushort miss, int score, ushort maxCombo, bool perfect)
    {
        var frame = new byte[LegacyFrames.OldScoreFrameLength];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(5), n300);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(7), n100);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(15), miss);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(17), score);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(21), maxCombo);
        frame[25] = perfect ? (byte)1 : (byte)0;
        frame[26] = 200;
        return frame;
    }

    private async Task WaitForAsync(string sql, long expected)
    {
        for (var i = 0; i < 100 && await ScalarAsync<long>(sql) != expected; i++)
            await Task.Delay(50);
    }
}
