using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using TomYum.Bancho;

namespace TomYum.Server.Bancho;

/// <summary>
/// Multiplayer match history, as osu! keeps it: every room, who joined, left or got host, and each
/// map played with everyone's final score, written to osu-web's <c>osu_mp</c> database, which the
/// website shows at <c>/community/matches/{id}</c>. When a room opens BanchoBot posts its link there.
/// </summary>
/// <remarks>
/// Packet handlers only queue what happened; one background loop writes it in order, so a slow
/// database never holds up the game. A room's database id is known once its row is in, and every
/// later write of that room comes after it in the queue.
/// </remarks>
public sealed class MatchHistory : BackgroundService
{
    // osu-web's event texts (App\Models\LegacyMatch\Event::EVENT_TYPES); a game's event has none.
    private const string Create = "CREATE";
    private const string Join = "JOIN";
    private const string Part = "PART";
    private const string Kick = "KICK";
    private const string Host = "HOST";
    private const string Disband = "DISBAND";

    private readonly string _connectionString;
    private readonly string _site;
    private readonly PlayerRegistry _players;
    private readonly ILogger<MatchHistory> _logger;
    private readonly Channel<Func<MySqlConnection, CancellationToken, Task>> _queue =
        Channel.CreateUnbounded<Func<MySqlConnection, CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Match, Room> _rooms = new();

    /// <summary>Creates the recorder.</summary>
    /// <param name="connectionString">osu-web's <c>osu_mp</c> database.</param>
    /// <param name="site">The website, e.g. <c>https://osu.example.com</c> (the link BanchoBot posts).</param>
    /// <param name="players">Online players, to post the link in the room's chat.</param>
    /// <param name="logger">Logger.</param>
    public MatchHistory(string connectionString, string site, PlayerRegistry players, ILogger<MatchHistory> logger)
    {
        ArgumentNullException.ThrowIfNull(site);
        _connectionString = connectionString;
        _site = site.TrimEnd('/');
        _players = players;
        _logger = logger;
    }

    /// <summary>A room was made (its host in slot 0).</summary>
    public void Created(Match match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var room = _rooms.GetOrAdd(match, _ => new Room());
        string name;
        bool isPrivate;
        int host;
        lock (match.SyncRoot)
        {
            name = match.Name;
            isPrivate = !string.IsNullOrEmpty(match.Password);
            host = match.HostUserId;
        }

        Enqueue(async (db, ct) =>
        {
            await using (var insert = new MySqlCommand(
                "INSERT INTO matches (start_time, name, private) VALUES (UTC_TIMESTAMP(), @name, @private)", db))
            {
                insert.Parameters.AddWithValue("@name", Truncate(name, 255));
                insert.Parameters.AddWithValue("@private", isPrivate);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                room.Id = insert.LastInsertedId;
            }

            await InsertEventAsync(db, room, host, Create, gameId: null, ct).ConfigureAwait(false);
            PostLink(match, room);
        });
    }

    /// <summary>A player took a slot.</summary>
    public void Joined(Match match, int userId) => Event(match, userId, Join);

    /// <summary>A player left (or lost their connection).</summary>
    public void Left(Match match, int userId) => Event(match, userId, Part);

    /// <summary>A player was kicked by the host or staff.</summary>
    public void Kicked(Match match, int userId) => Event(match, userId, Kick);

    /// <summary>Host went to <paramref name="userId"/>.</summary>
    public void HostChanged(Match match, int userId) => Event(match, userId, Host);

    /// <summary>A map started: the game, and who's playing it (slot, team, mods).</summary>
    public void GameStarted(Match match)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (!_rooms.TryGetValue(match, out var room))
            return;

        var game = new Game();
        int mapId;
        byte mode, scoring, teamType;
        uint mods;
        lock (match.SyncRoot)
        {
            mapId = match.MapId;
            mode = match.Mode;
            scoring = (byte)match.WinCondition;
            teamType = (byte)match.TeamType;
            mods = match.Mods;
            for (var slot = 0; slot < match.Slots.Length; slot++)
            {
                var s = match.Slots[slot];
                if (s.Status == SlotStatus.Playing)
                    game.Players[slot] = new Player(s.UserId, s.Team, match.FreeMods ? s.Mods : null);
            }
        }

        if (Interlocked.Exchange(ref room.Game, game) is { } unfinished)
            EndGame(unfinished, aborted: true);
        Enqueue(async (db, ct) =>
        {
            if (room.Id is not { } matchId)
                return;
            await using (var insert = new MySqlCommand(
                "INSERT INTO games (match_id, start_time, beatmap_id, play_mode, match_type, scoring_type, team_type, mods) " +
                "VALUES (@match, UTC_TIMESTAMP(), @map, @mode, 0, @scoring, @team, @mods)", db))
            {
                insert.Parameters.AddWithValue("@match", matchId);
                insert.Parameters.AddWithValue("@map", mapId is > 0 and <= 0xFFFFFF ? mapId : DBNull.Value);
                insert.Parameters.AddWithValue("@mode", mode);
                insert.Parameters.AddWithValue("@scoring", scoring);
                insert.Parameters.AddWithValue("@team", teamType);
                insert.Parameters.AddWithValue("@mods", mods);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                game.Id = insert.LastInsertedId;
            }

            await InsertEventAsync(db, room, userId: null, text: null, game.Id, ct).ConfigureAwait(false);
        });
    }

    /// <summary>A player's score frame (MATCH_SCORE_UPDATE): the last one is their final score.</summary>
    public void ScoreFrame(Match match, int slot, ReadOnlySpan<byte> frame)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (frame.Length < LegacyFrames.OldScoreFrameLength || !_rooms.TryGetValue(match, out var room) ||
            room.Game?.Players.GetValueOrDefault(slot) is not { } player)
            return;

        lock (player)
        {
            player.Frame = frame[..LegacyFrames.OldScoreFrameLength].ToArray();
        }
    }

    /// <summary>A player failed the map (they don't pass).</summary>
    public void Failed(Match match, int slot)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (_rooms.TryGetValue(match, out var room) && room.Game?.Players.GetValueOrDefault(slot) is { } player)
            player.Failed = true;
    }

    /// <summary>The map ended: everyone's final scores.</summary>
    public void GameEnded(Match match) => EndGame(match, aborted: false);

    /// <summary>The map was aborted: the game ends with no scores.</summary>
    public void GameAborted(Match match) => EndGame(match, aborted: true);

    /// <summary>The room closed (its last player left, or staff closed it).</summary>
    public void Disbanded(Match match)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (!_rooms.TryRemove(match, out var room))
            return;
        if (Interlocked.Exchange(ref room.Game, null) is { } unfinished)
            EndGame(unfinished, aborted: true);

        Enqueue(async (db, ct) =>
        {
            if (room.Id is not { } matchId)
                return;
            await InsertEventAsync(db, room, userId: null, Disband, gameId: null, ct).ConfigureAwait(false);
            await using var end = new MySqlCommand("UPDATE matches SET end_time = UTC_TIMESTAMP() WHERE match_id = @id", db);
            end.Parameters.AddWithValue("@id", matchId);
            await end.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        });
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Rooms open when the server last stopped are gone: they end now.
        await RunAsync(async (db, ct) =>
        {
            await using var close = new MySqlCommand(
                "UPDATE games SET end_time = UTC_TIMESTAMP() WHERE end_time IS NULL; " +
                "UPDATE matches SET end_time = UTC_TIMESTAMP() WHERE end_time IS NULL", db);
            var closed = await close.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (closed > 0)
                _logger.LogInformation("Closed {Count} match history row(s) left open by the last run.", closed);
        }, stoppingToken).ConfigureAwait(false);

        await foreach (var write in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            await RunAsync(write, stoppingToken).ConfigureAwait(false);
    }

    private void Event(Match match, int userId, string text)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (!_rooms.TryGetValue(match, out var room))
            return;
        Enqueue((db, ct) => InsertEventAsync(db, room, userId, text, gameId: null, ct));
    }

    private void EndGame(Match match, bool aborted)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (_rooms.TryGetValue(match, out var room) && Interlocked.Exchange(ref room.Game, null) is { } game)
            EndGame(game, aborted);
    }

    private void EndGame(Game game, bool aborted)
    {
        Enqueue(async (db, ct) =>
        {
            if (game.Id is not { } gameId)
                return;
            await using (var end = new MySqlCommand("UPDATE games SET end_time = UTC_TIMESTAMP() WHERE game_id = @id", db))
            {
                end.Parameters.AddWithValue("@id", gameId);
                await end.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (aborted)
                return;
            foreach (var (slot, player) in game.Players)
            {
                byte[]? frame;
                lock (player)
                {
                    frame = player.Frame;
                }

                await using var insert = new MySqlCommand(
                    "INSERT IGNORE INTO game_scores (game_id, slot, team, user_id, score, maxcombo, count50, count100, count300, " +
                    "countmiss, countgeki, countkatu, perfect, pass, enabled_mods) VALUES (@game, @slot, @team, @user, @score, " +
                    "@combo, @n50, @n100, @n300, @miss, @geki, @katu, @perfect, @pass, @mods)", db);
                insert.Parameters.AddWithValue("@game", gameId);
                insert.Parameters.AddWithValue("@slot", slot);
                insert.Parameters.AddWithValue("@team", player.Team);
                insert.Parameters.AddWithValue("@user", player.UserId);
                insert.Parameters.AddWithValue("@mods", player.Mods is { } mods ? (int)mods : DBNull.Value);
                // The frame: time i32, id u8, 300 100 50 geki katu miss (u16 each), score i32,
                // max combo u16, combo u16, perfect bool, hp u8, ...
                var f = frame ?? new byte[LegacyFrames.OldScoreFrameLength];
                insert.Parameters.AddWithValue("@n300", BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(5)));
                insert.Parameters.AddWithValue("@n100", BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(7)));
                insert.Parameters.AddWithValue("@n50", BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(9)));
                insert.Parameters.AddWithValue("@geki", BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(11)));
                insert.Parameters.AddWithValue("@katu", BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(13)));
                insert.Parameters.AddWithValue("@miss", BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(15)));
                insert.Parameters.AddWithValue("@score", BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(17)));
                insert.Parameters.AddWithValue("@combo", BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(21)));
                insert.Parameters.AddWithValue("@perfect", f[25] != 0);
                insert.Parameters.AddWithValue("@pass", frame is not null && !player.Failed);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        });
    }

    private static async Task InsertEventAsync(MySqlConnection db, Room room, int? userId, string? text, long? gameId, CancellationToken ct)
    {
        if (room.Id is not { } matchId)
            return;
        await using var insert = new MySqlCommand(
            "INSERT INTO events (match_id, game_id, user_id, text, timestamp) VALUES (@match, @game, @user, @text, UTC_TIMESTAMP())", db);
        insert.Parameters.AddWithValue("@match", matchId);
        insert.Parameters.AddWithValue("@game", gameId is { } g ? g : DBNull.Value);
        insert.Parameters.AddWithValue("@user", userId is > 0 ? userId : DBNull.Value);
        insert.Parameters.AddWithValue("@text", text is null ? DBNull.Value : text);
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>BanchoBot's "Match history available here." in the room's chat, as on osu!.</summary>
    private void PostLink(Match match, Room room)
    {
        if (room.Id is not { } id)
            return;
        var url = string.Create(CultureInfo.InvariantCulture, $"{_site}/community/matches/{id}");
        var writer = new BanchoPacketWriter(160);
        ChatEncoder.WriteMessage(writer, new BanchoMessage(BanchoBot.Name, $"Match history available [{url} here].", "#multiplayer", BanchoBot.UserId));
        var bytes = writer.ToArray();
        foreach (var member in match.Channel.Members)
            member.Enqueue(bytes);
    }

    private void Enqueue(Func<MySqlConnection, CancellationToken, Task> write) => _queue.Writer.TryWrite(write);

    private async Task RunAsync(Func<MySqlConnection, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = new MySqlConnection(_connectionString);
            await db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await write(db, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // history is best effort: a failed write never stops the game or the loop
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Couldn't write match history.");
        }
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>A room's history: its database id, and the map being played.</summary>
    private sealed class Room
    {
        public long? Id { get; set; }

#pragma warning disable SA1401, CA1051 // swapped atomically (Interlocked.Exchange) when the game ends
        public Game? Game;
#pragma warning restore SA1401, CA1051
    }

    private sealed class Game
    {
        public long? Id { get; set; }

        public Dictionary<int, Player> Players { get; } = [];
    }

    private sealed class Player(int userId, byte team, uint? mods)
    {
        public int UserId { get; } = userId;

        public byte Team { get; } = team;

        public uint? Mods { get; } = mods;

        public byte[]? Frame { get; set; }

        public bool Failed { get; set; }
    }
}
