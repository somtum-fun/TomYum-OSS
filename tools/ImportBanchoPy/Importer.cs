using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using StackExchange.Redis;
using TomYum.Caching;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Tools.ImportBanchoPy;

/// <summary>
/// The import itself: a plan (counts, nothing written) and the steps, each recorded in
/// <c>tomyum.bpy_import_steps</c> so a run that stopped halfway continues where it was. Players keep
/// their ids through <c>tomyum.bpy_import_user_map</c> (old id -> new id), which every step joins.
/// </summary>
internal sealed class Importer
{
    /// <summary>The steps in the order they run. <c>custom_sets</c> runs on its own, with the server up.</summary>
    public static readonly string[] Steps =
    [
        "users", "groups", "moderation", "connections", "friends", "beatmaps", "scores", "replays", "stats",
        "achievements", "rank_history", "favourites", "hashes", "staff_log", "nominations", "mirror",
    ];

    private const string UserMapTable = "bpy_import_user_map";
    private const string StepsTable = "bpy_import_steps";

    private readonly Db _db;
    private readonly SourceSchema _source;
    private readonly ImportSettings _settings;
    private readonly IServiceProvider _services;
    private readonly StreamWriter _log;

    private readonly string S; // bancho.py's schema
    private readonly string T; // TomYum's
    private readonly string W; // osu-web's
    private readonly string Map; // the id map

    public Importer(Db db, SourceSchema source, ImportSettings settings, IServiceProvider services, StreamWriter log)
    {
        _db = db;
        _source = source;
        _settings = settings;
        _services = services;
        _log = log;
        S = Db.Identifier(source.Schema);
        T = Db.Identifier(settings.TomYum);
        W = Db.Identifier(settings.OsuWeb);
        Map = T + "." + UserMapTable;
    }

    private PrivilegeLayout P => _source.Privileges;

    private void Say(string line)
    {
        Console.WriteLine(line);
        _log.WriteLine(line);
    }

    // ------------------------------------------------------------------------------------------
    // Source indexes

    /// <summary>
    /// Indexes the copy of the old database needs for the joins here (a dump often carries only the
    /// primary keys, and <c>scores</c> without an index on <c>map_md5</c> makes a count take minutes).
    /// It is a staging copy, so adding them is fine.
    /// </summary>
    public async Task EnsureSourceIndexesAsync()
    {
        var wanted = new (string Table, string Columns)[]
        {
            ("scores", "map_md5"), ("scores", "userid"), ("scores", "status"),
            ("logs", "`to`"), ("client_hashes", "userid"), ("user_achievements", "userid"), ("stats", "id"),
            ("relationships", "user1"), ("favourites", "userid"), ("ratings", "userid"), ("ratings", "map_md5"),
            ("ranking_history", "user_id, mode"), ("beatmap_nominations", "set_id"), ("user_connections", "user_id"),
        };
        foreach (var (table, columns) in wanted)
        {
            if (await _db.TableExistsAsync(_source.Schema, table).ConfigureAwait(false) == 0)
                continue;
            var first = columns.Split(',')[0].Trim().Trim('`');
            if (await _db.ColumnExistsAsync(_source.Schema, table, first).ConfigureAwait(false) == 0)
                continue;
            var indexed = await _db.CountAsync(
                "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = @s AND table_name = @t AND seq_in_index = 1 AND column_name = @c",
                ("@s", _source.Schema), ("@t", table), ("@c", first)).ConfigureAwait(false);
            if (indexed > 0)
                continue;
            var name = "bpy_import_" + first;
            try
            {
                await _db.ExecAsync($"ALTER TABLE {S}.{Db.Identifier(table)} ADD INDEX {Db.Identifier(name)} ({columns})").ConfigureAwait(false);
                Say($"  index added on {table}({columns})");
            }
            catch (MySqlException ex)
            {
                // An old table definition MySQL 8's strict mode won't rewrite (a zero default on a datetime, say): go without.
                Say($"  no index on {table}({columns}): {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Plan

    /// <summary>Counts what the import would do. Writes nothing (the id map is built in a temporary table).</summary>
    public async Task<List<PlanLine>> PlanAsync()
    {
        var lines = new List<PlanLine>();
        var p = P;

        var users = await _db.CountAsync($"SELECT COUNT(*) FROM {S}.users WHERE id <> {Db.N(_settings.OldBotId)}").ConfigureAwait(false);
        lines.Add(new("players (without the old bot)", users));
        var remapped = await _db.CountAsync($"SELECT COUNT(*) FROM {S}.users WHERE id = {Db.N(_settings.BotUserId)}").ConfigureAwait(false);
        if (remapped > 0 && !_settings.Remaps.ContainsKey(_settings.BotUserId))
            lines.Add(new("players holding BanchoBot's id", remapped, $"old id {_settings.BotUserId} is BanchoBot's here: add --remap {_settings.BotUserId}=<new id>, or run TomYum with TOMYUM_BANCHO__BOTUSERID set to a free id (2, if nobody has it)"));
        var lowIds = await _db.CountAsync($"SELECT COUNT(*) FROM {S}.users WHERE id <= 1 AND id <> {Db.N(_settings.OldBotId)}").ConfigureAwait(false);
        if (lowIds > 0)
            lines.Add(new("players with id 1 or lower", lowIds, "lazer can't show id 1: add --remap for them"));

        var refused = await _db.QueryAsync(
            $"SELECT id, name FROM {S}.users WHERE id <> {Db.N(_settings.OldBotId)} AND (LENGTH(name) < 3 OR LENGTH(name) > 15 OR name NOT REGEXP '^[A-Za-z0-9_ \\\\[\\\\]-]+$' OR name LIKE '%  %' OR (name LIKE '%\\\\_%' AND name LIKE '% %') OR name LIKE ' %' OR name LIKE '% ')",
            r => (Id: r.GetInt32(0), Name: r.GetString(1))).ConfigureAwait(false);
        lines.Add(new("names outside osu-web's rules", refused.Count, refused.Count == 0 ? null : $"imported as they are; listed in {Path.Combine(_settings.OutDir, "refused-names.csv")}"));
        await File.WriteAllLinesAsync(Path.Combine(_settings.OutDir, "refused-names.csv"),
            refused.Select(r => $"{Db.N(r.Id)},{Csv(r.Name)}").Prepend("user_id,username")).ConfigureAwait(false);

        var dupeNames = await _db.CountAsync($"SELECT COUNT(*) - COUNT(DISTINCT LOWER(name)) FROM {S}.users").ConfigureAwait(false);
        if (dupeNames > 0)
            lines.Add(new("names that clash case-insensitively", dupeNames, "osu-web's username_clean is unique: rename one of each pair first"));
        var dupeMails = await _db.CountAsync($"SELECT COUNT(*) - COUNT(DISTINCT LOWER(email)) FROM {S}.users WHERE email <> ''").ConfigureAwait(false);
        if (dupeMails > 0)
            lines.Add(new("emails on more than one account", dupeMails, "osu-web's user_email is unique: the import fails on the second one"));

        lines.Add(new("restricted", await _db.CountAsync($"SELECT COUNT(*) FROM {S}.users WHERE id <> {Db.N(_settings.OldBotId)} AND (priv & {Db.N(p.Unrestricted)}) = 0").ConfigureAwait(false)));
        lines.Add(new("supporters (still running)", await _db.CountAsync($"SELECT COUNT(*) FROM {S}.users WHERE (priv & {Db.N(p.Donor)}) <> 0 AND donor_end > UNIX_TIMESTAMP()").ConfigureAwait(false)));
        foreach (var (bit, group, _) in p.Groups())
        {
            var n = await _db.CountAsync($"SELECT COUNT(*) FROM {S}.users WHERE id <> {Db.N(_settings.OldBotId)} AND (priv & {Db.N(bit)}) <> 0").ConfigureAwait(false);
            var exists = await _db.CountAsync($"SELECT COUNT(*) FROM {W}.phpbb_groups WHERE identifier = @g", ("@g", group)).ConfigureAwait(false) > 0;
            lines.Add(new($"group {group}", n, exists || n == 0 ? null : "the website has no such group: these players get no group"));
        }
        if (_source.HasOldName)
            lines.Add(new("players with previous names", await _db.CountAsync($"SELECT COUNT(*) FROM {S}.users WHERE old_name IS NOT NULL AND old_name <> ''").ConfigureAwait(false)));

        lines.Add(new("friendships", await _db.CountAsync($"SELECT COUNT(*) FROM {S}.relationships" + (_source.HasRelationshipType ? " WHERE type = 'friend'" : "")).ConfigureAwait(false)));
        if (_source.HasConnections)
            lines.Add(new("Discord links (OAuth, verified)", await _db.CountAsync($"SELECT COUNT(*) FROM {S}.user_connections WHERE kind = 'discord' AND verified = 1").ConfigureAwait(false)));

        var scores = await _db.CountAsync($"SELECT COUNT(*) FROM {S}.scores").ConfigureAwait(false);
        var passed = await _db.CountAsync($"SELECT COUNT(*) FROM {S}.scores WHERE status > 0").ConfigureAwait(false);
        lines.Add(new("scores (passed)", scores, $"{Db.N(passed)} passed, ids up to {Db.N(await _db.CountAsync($"SELECT COALESCE(MAX(id), 0) FROM {S}.scores").ConfigureAwait(false))}"));
        lines.Add(new("scores on maps the old server doesn't know", await _db.CountAsync($"SELECT COUNT(*) FROM {S}.scores s WHERE NOT EXISTS (SELECT 1 FROM {S}.maps b WHERE b.md5 = s.map_md5)").ConfigureAwait(false), "kept without a map; the website shows them once the map is imported"));
        lines.Add(new("scores on private sets", await _db.CountAsync($"SELECT COUNT(*) FROM {S}.scores s JOIN {S}.maps b ON b.md5 = s.map_md5 WHERE {PrivateMap("b")}").ConfigureAwait(false), "re-pointed at the uploaded sets by --custom-sets"));
        lines.Add(new("maps to import (played or staff-frozen)", await _db.CountAsync($"SELECT COUNT(*) FROM {S}.maps b WHERE NOT ({PrivateMap("b")}) AND (b.frozen = 1 OR b.md5 IN (SELECT DISTINCT s.map_md5 FROM {S}.scores s))").ConfigureAwait(false)));
        lines.Add(new("staff-frozen statuses", await _db.CountAsync($"SELECT COUNT(*) FROM {S}.maps WHERE frozen = 1").ConfigureAwait(false)));
        var privateSets = await _db.QueryAsync($"SELECT DISTINCT set_id FROM {S}.maps b WHERE {PrivateMap("b")}", r => r.GetInt32(0)).ConfigureAwait(false);
        var oszMissing = _settings.DataPath is null ? privateSets.Count : privateSets.Count(id => !File.Exists(Path.Combine(_settings.DataPath, "osz", Db.N(id) + ".osz")));
        lines.Add(new("private sets", privateSets.Count, privateSets.Count == 0 ? null : oszMissing == 0 ? "every .osz is in --data/osz" : $"{Db.N(oszMissing)} without an .osz in --data/osz"));

        if (_settings.DataPath is not null)
        {
            var osr = Path.Combine(_settings.DataPath, "osr");
            var have = Directory.Exists(osr) ? Directory.EnumerateFiles(osr, "*.osr").Select(f => Path.GetFileNameWithoutExtension(f)).ToHashSet(StringComparer.Ordinal) : [];
            var ids = await _db.QueryAsync($"SELECT id FROM {S}.scores WHERE status > 0", r => r.GetInt64(0)).ConfigureAwait(false);
            var missing = ids.Count(id => !have.Contains(Db.N(id)));
            lines.Add(new("replays for passed scores", ids.Count - missing, missing == 0 ? null : $"{Db.N(missing)} passed scores have no .osr in --data/osr"));
        }
        else
        {
            lines.Add(new("replays", 0, "no --data: no replays are copied"));
        }

        lines.Add(new("stats rows (modes with plays)", await _db.CountAsync($"SELECT COUNT(*) FROM {S}.stats WHERE mode <> 7 AND (plays > 0 OR pp > 0)").ConfigureAwait(false)));
        var medals = await _db.CountAsync($"SELECT COUNT(*) FROM {S}.user_achievements ua JOIN {S}.achievements a ON a.id = ua.achid JOIN {W}.osu_achievements oa ON {Bin("oa.slug")} = {Bin("a.file")}").ConfigureAwait(false);
        var custom = await _db.CountAsync($"SELECT COUNT(*) FROM {S}.achievements a WHERE NOT EXISTS (SELECT 1 FROM {W}.osu_achievements oa WHERE {Bin("oa.slug")} = {Bin("a.file")})").ConfigureAwait(false);
        lines.Add(new("medal unlocks (osu!'s medals, by slug)", medals));
        lines.Add(new("medals the website doesn't have", custom, custom == 0 ? null : "the server's own medals: bring them with `php artisan tomyum:custom-medals-import` afterwards"));
        if (_source.HasRankingHistory)
            lines.Add(new("rank history rows", await _db.CountAsync($"SELECT COUNT(*) FROM {S}.ranking_history").ConfigureAwait(false), "the last 89 days go into the website's graph"));
        if (_source.HasFavourites)
            lines.Add(new("favourites", await _db.CountAsync($"SELECT COUNT(*) FROM {S}.favourites").ConfigureAwait(false)));
        lines.Add(new("device hashes", await _db.CountAsync($"SELECT COUNT(*) FROM {S}.client_hashes").ConfigureAwait(false)));
        lines.Add(new("staff log rows", await _db.CountAsync($"SELECT COUNT(*) FROM {S}.logs").ConfigureAwait(false)));
        if (_source.HasNominations)
            lines.Add(new("nomination history rows", await _db.CountAsync($"SELECT COUNT(*) FROM {S}.beatmap_nominations").ConfigureAwait(false)));
        if (_source.HasClans)
            lines.Add(new("clans", await _db.CountAsync($"SELECT COUNT(*) FROM {S}.clans").ConfigureAwait(false), "become teams with `php artisan tomyum:import-bpy-media --teams` (it needs the website's code)"));

        return lines;
    }

    private string PrivateMap(string alias) =>
        _source.HasMapServer ? $"({alias}.server = 'private' OR {alias}.id >= 100000000)" : $"{alias}.id >= 100000000";

    /// <summary>A string expression comparable across schemas whose collations differ (the website's 0900_ai_ci, bancho.py's general_ci).</summary>
    private static string Bin(string expression) => $"CONVERT({expression} USING utf8mb4) COLLATE utf8mb4_bin";

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    // ------------------------------------------------------------------------------------------
    // Guards and bookkeeping

    /// <summary>Why the target can't be written to, or null.</summary>
    public async Task<string?> CheckTargetAsync()
    {
        var players = await _db.CountAsync($"SELECT COUNT(*) FROM {W}.phpbb_users WHERE user_id <> {Db.N(_settings.BotUserId)}").ConfigureAwait(false);
        var scores = await _db.CountAsync($"SELECT COUNT(*) FROM {T}.scores").ConfigureAwait(false);
        var done = await _db.TableExistsAsync(_settings.TomYum, StepsTable).ConfigureAwait(false) > 0
            && await _db.CountAsync($"SELECT COUNT(*) FROM {T}.{StepsTable}").ConfigureAwait(false) > 0;
        if (!done && !_settings.AllowExisting && (players > 0 || scores > 0))
            return $"the target already has {Db.N(players)} player(s) and {Db.N(scores)} score(s). The import is meant for an empty server (osu-web migrated, TomYum started once for BanchoBot). --allow-existing goes ahead anyway; clashing ids then fail.";
        if (await _db.CountAsync($"SELECT COUNT(*) FROM {W}.phpbb_groups WHERE identifier = 'default'").ConfigureAwait(false) == 0)
            return "osu-web's groups aren't seeded (no `default` group): run `php artisan tomyum:setup` (or its GroupSeeder) first.";
        if (await _db.CountAsync($"SELECT COUNT(*) FROM {W}.phpbb_users WHERE user_id = {Db.N(_settings.BotUserId)}").ConfigureAwait(false) == 0)
            return $"osu-web has no BanchoBot at id {Db.N(_settings.BotUserId)}: start TomYum once (it creates the bot), then stop it and run the import.";
        return null;
    }

    public async Task EnsureTablesAsync()
    {
        await _db.ExecAsync($"CREATE TABLE IF NOT EXISTS {T}.{StepsTable} (step VARCHAR(32) NOT NULL PRIMARY KEY, finished_at DATETIME(6) NOT NULL, notes VARCHAR(255) NULL)").ConfigureAwait(false);
        await _db.ExecAsync($"CREATE TABLE IF NOT EXISTS {Map} (old_id INT NOT NULL PRIMARY KEY, new_id INT NOT NULL, UNIQUE KEY (new_id))").ConfigureAwait(false);
    }

    public async Task<HashSet<string>> FinishedStepsAsync() =>
        (await _db.QueryAsync($"SELECT step FROM {T}.{StepsTable}", r => r.GetString(0)).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);

    private Task<int> MarkDoneAsync(string step, string notes) =>
        _db.ExecAsync($"INSERT INTO {T}.{StepsTable} (step, finished_at, notes) VALUES (@s, UTC_TIMESTAMP(6), @n) ON DUPLICATE KEY UPDATE finished_at = VALUES(finished_at), notes = VALUES(notes)",
            ("@s", step), ("@n", notes.Length > 255 ? notes[..255] : notes));

    /// <summary>Fills the id map: every old player except the old bot, remapped where asked.</summary>
    public async Task BuildUserMapAsync()
    {
        await _db.ExecAsync($"DELETE FROM {Map}").ConfigureAwait(false);
        var skip = _settings.SkipEmptyRestricted
            ? $" AND NOT ((u.priv & {Db.N(P.Unrestricted)}) = 0 AND NOT EXISTS (SELECT 1 FROM {S}.scores s WHERE s.userid = u.id) AND NOT EXISTS (SELECT 1 FROM {S}.client_hashes h WHERE h.userid = u.id) AND u.latest_activity <= u.creation_time)"
            : "";
        await _db.ExecAsync($"INSERT INTO {Map} (old_id, new_id) SELECT u.id, u.id FROM {S}.users u WHERE u.id <> {Db.N(_settings.OldBotId)}{skip}").ConfigureAwait(false);
        foreach (var (oldId, newId) in _settings.Remaps)
            await _db.ExecAsync($"UPDATE {Map} SET new_id = {Db.N(newId)} WHERE old_id = {Db.N(oldId)}").ConfigureAwait(false);

        var clash = await _db.CountAsync($"SELECT COUNT(*) FROM {Map} WHERE new_id = {Db.N(_settings.BotUserId)} OR new_id <= 1").ConfigureAwait(false);
        if (clash > 0)
            throw new InvalidOperationException($"{Db.N(clash)} player(s) would get BanchoBot's id ({Db.N(_settings.BotUserId)}) or an id below 2. Use --remap OLD=NEW for them (see the plan).");
    }

    // ------------------------------------------------------------------------------------------
    // Steps

    /// <summary>Runs <paramref name="step"/> and records it. Returns a one-line summary.</summary>
    public async Task<string> RunStepAsync(string step)
    {
        var notes = step switch
        {
            "users" => await UsersAsync().ConfigureAwait(false),
            "groups" => await GroupsAsync().ConfigureAwait(false),
            "moderation" => await ModerationAsync().ConfigureAwait(false),
            "connections" => await ConnectionsAsync().ConfigureAwait(false),
            "friends" => await FriendsAsync().ConfigureAwait(false),
            "beatmaps" => await BeatmapsAsync().ConfigureAwait(false),
            "scores" => await ScoresAsync().ConfigureAwait(false),
            "replays" => await ReplaysAsync().ConfigureAwait(false),
            "stats" => await StatsAsync().ConfigureAwait(false),
            "achievements" => await AchievementsAsync().ConfigureAwait(false),
            "rank_history" => await RankHistoryAsync().ConfigureAwait(false),
            "favourites" => await FavouritesAsync().ConfigureAwait(false),
            "hashes" => await HashesAsync().ConfigureAwait(false),
            "staff_log" => await StaffLogAsync().ConfigureAwait(false),
            "nominations" => await NominationsAsync().ConfigureAwait(false),
            "mirror" => await MirrorAsync().ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown step '{step}'.", nameof(step)),
        };
        await MarkDoneAsync(step, notes).ConfigureAwait(false);
        return notes;
    }

    private async Task<string> UsersAsync()
    {
        var p = P;
        var location = _source.HasLocation ? "LEFT(COALESCE(u.location, ''), 100)" : "''";
        var donor = $"((u.priv & {Db.N(p.Donor)}) <> 0 AND u.donor_end > UNIX_TIMESTAMP())";
        var inserted = await _db.ExecAsync($"""
            INSERT INTO {W}.phpbb_users (user_id, user_type, group_id, user_colour, user_rank, username, username_clean, user_email,
                user_password, user_passchg, user_regdate, user_lastvisit, user_ip, user_lang, country_acronym, osu_playmode, osu_playstyle,
                user_from, user_sig, user_occ, user_interests, user_warnings, osu_subscriber, osu_subscriptionexpiry)
            SELECT m.new_id, 0, g.group_id, g.group_colour, g.group_rank, u.name, LOWER(u.name),
                IF(CHAR_LENGTH(u.email) BETWEEN 3 AND 100, u.email, NULL),
                u.pw_bcrypt, u.creation_time, u.creation_time, GREATEST(u.latest_activity, u.creation_time), '', '',
                IF(LOWER(u.country) IN ('xx', ''), '', UPPER(u.country)), IF(u.preferred_mode BETWEEN 0 AND 3, u.preferred_mode, 0), 0,
                {location}, '', '', '', 0, IF({donor}, 1, 0), IF({donor}, FROM_UNIXTIME(u.donor_end), NULL)
            FROM {S}.users u JOIN {Map} m ON m.old_id = u.id
            CROSS JOIN {W}.phpbb_groups g
            WHERE g.identifier = 'default'
            """).ConfigureAwait(false);
        await _db.ExecAsync($"""
            INSERT IGNORE INTO {W}.phpbb_user_group (group_id, user_id, group_leader, user_pending, playmodes)
            SELECT g.group_id, m.new_id, 0, 0, NULL FROM {Map} m CROSS JOIN {W}.phpbb_groups g WHERE g.identifier = 'default'
            """).ConfigureAwait(false);
        var next = await _db.CountAsync($"SELECT GREATEST(COALESCE(MAX(user_id), 0) + 1, {Db.N(_settings.BotUserId + 1)}) FROM {W}.phpbb_users").ConfigureAwait(false);
        await _db.ExecAsync($"ALTER TABLE {W}.phpbb_users AUTO_INCREMENT = {Db.N(next)}").ConfigureAwait(false);
        await _db.ExecAsync($"INSERT INTO {W}.osu_counts (name, count) SELECT 'usercount', COUNT(*) FROM {W}.phpbb_users ON DUPLICATE KEY UPDATE count = VALUES(count)").ConfigureAwait(false);

        // Players who logged in on the old server skip TomYum's ban-evasion check for known players.
        var verified = await _db.ExecAsync($"""
            INSERT IGNORE INTO {T}.verified_users (user_id, verified_at, verified_by)
            SELECT m.new_id, FROM_UNIXTIME(GREATEST(u.creation_time, 1)), 0 FROM {S}.users u JOIN {Map} m ON m.old_id = u.id WHERE (u.priv & {Db.N(p.Verified)}) <> 0
            """).ConfigureAwait(false);

        var whitelisted = 0;
        if (await _db.TableExistsAsync(_settings.OsuWeb, "tomyum_account_flags").ConfigureAwait(false) > 0)
        {
            whitelisted = await _db.ExecAsync($"""
                INSERT IGNORE INTO {W}.tomyum_account_flags (user_id, whitelisted_at, whitelisted_by, whitelist_reason)
                SELECT m.new_id, UTC_TIMESTAMP(), NULL, 'Whitelisted on the previous server (bancho.py).' FROM {S}.users u JOIN {Map} m ON m.old_id = u.id WHERE (u.priv & {Db.N(p.Whitelisted)}) <> 0
                """).ConfigureAwait(false);
        }

        var oldNames = 0;
        if (_source.HasOldName)
        {
            var rows = await _db.QueryAsync($"SELECT m.new_id, u.name, u.old_name FROM {S}.users u JOIN {Map} m ON m.old_id = u.id WHERE u.old_name IS NOT NULL AND u.old_name <> ''",
                r => (Id: r.GetInt32(0), Name: r.GetString(1), Old: r.GetString(2))).ConfigureAwait(false);
            foreach (var row in rows)
            {
                // old_name is a comma-separated list, oldest first; chain them up to today's name. osu-web only
                // shows and searches support/paid/admin rows (admin doesn't use up a paid rename).
                var names = row.Old.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Append(row.Name).ToList();
                for (var i = 0; i + 1 < names.Count; i++)
                {
                    if (names[i].Length == 0 || names[i].Length > 30 || string.Equals(names[i], names[i + 1], StringComparison.Ordinal))
                        continue;
                    await _db.ExecAsync($"INSERT INTO {W}.osu_username_change_history (user_id, username, username_last, type, timestamp) VALUES (@u, @n, @l, 'admin', UTC_TIMESTAMP() - INTERVAL {Db.N(names.Count - i)} SECOND)",
                        ("@u", row.Id), ("@n", names[i + 1].Length > 30 ? names[i + 1][..30] : names[i + 1]), ("@l", names[i])).ConfigureAwait(false);
                    oldNames++;
                }
            }
        }
        return $"{Db.N(inserted)} accounts, {Db.N(verified)} verified, {Db.N(whitelisted)} whitelisted, {Db.N(oldNames)} previous names; phpbb_users AUTO_INCREMENT {Db.N(next)}";
    }

    private async Task<string> GroupsAsync()
    {
        var parts = new List<string>();
        foreach (var (bit, group, rulesets) in P.Groups())
        {
            if (await _db.CountAsync($"SELECT COUNT(*) FROM {W}.phpbb_groups WHERE identifier = @g", ("@g", group)).ConfigureAwait(false) == 0)
            {
                Say($"  group {group}: the website has no such group, skipped");
                continue;
            }
            var playmodes = rulesets ? "'[\"osu\", \"taiko\", \"fruits\", \"mania\"]'" : "NULL";
            var n = await _db.ExecAsync($"""
                INSERT IGNORE INTO {W}.phpbb_user_group (group_id, user_id, group_leader, user_pending, playmodes)
                SELECT g.group_id, m.new_id, 0, 0, {playmodes} FROM {S}.users u JOIN {Map} m ON m.old_id = u.id
                CROSS JOIN {W}.phpbb_groups g WHERE g.identifier = @g AND (u.priv & {Db.N(bit)}) <> 0
                """, ("@g", group)).ConfigureAwait(false);
            parts.Add($"{group} {Db.N(n)}");
        }
        return parts.Count == 0 ? "no groups" : string.Join(", ", parts);
    }

    private async Task<string> ModerationAsync()
    {
        var restricted = await _db.ExecAsync($"""
            UPDATE {W}.phpbb_users pu JOIN {Map} m ON m.new_id = pu.user_id JOIN {S}.users u ON u.id = m.old_id
            SET pu.user_warnings = 1 WHERE (u.priv & {Db.N(P.Unrestricted)}) = 0
            """).ConfigureAwait(false);
        await _db.ExecAsync($"""
            INSERT INTO {W}.osu_user_banhistory (user_id, reason, ban_status, period, timestamp, banner_id, permanent)
            SELECT m.new_id,
                LEFT(COALESCE((SELECT l.msg FROM {S}.logs l WHERE l.`to` = u.id AND l.action = 'restrict' ORDER BY l.time DESC, l.id DESC LIMIT 1), 'Restricted on the previous server (bancho.py).'), 8000),
                1, 0,
                COALESCE((SELECT l.time FROM {S}.logs l WHERE l.`to` = u.id AND l.action = 'restrict' ORDER BY l.time DESC, l.id DESC LIMIT 1), FROM_UNIXTIME(GREATEST(u.latest_activity, u.creation_time, 1))),
                (SELECT ma.new_id FROM {S}.logs l JOIN {Map} ma ON ma.old_id = l.`from` WHERE l.`to` = u.id AND l.action = 'restrict' ORDER BY l.time DESC, l.id DESC LIMIT 1),
                1
            FROM {S}.users u JOIN {Map} m ON m.old_id = u.id WHERE (u.priv & {Db.N(P.Unrestricted)}) = 0
            """).ConfigureAwait(false);
        var silenced = await _db.ExecAsync($"""
            INSERT INTO {W}.osu_user_banhistory (user_id, reason, ban_status, period, timestamp, banner_id, permanent)
            SELECT m.new_id, 'Silenced on the previous server (bancho.py).', 2, u.silence_end - UNIX_TIMESTAMP(), UTC_TIMESTAMP(), NULL, 0
            FROM {S}.users u JOIN {Map} m ON m.old_id = u.id WHERE u.silence_end > UNIX_TIMESTAMP()
            """).ConfigureAwait(false);
        return $"{Db.N(restricted)} restricted, {Db.N(silenced)} silenced";
    }

    private const string DiscordId = "REGEXP '^[0-9]{5,20}$'";

    private async Task<string> ConnectionsAsync()
    {
        if (!_source.HasConnections)
            return "no user_connections table: nothing to do";
        var discord = await _db.ExecAsync($"""
            INSERT IGNORE INTO {T}.discord_links (user_id, discord_id, linked_at)
            SELECT m.new_id, CAST(c.value AS UNSIGNED), c.connected_at FROM {S}.user_connections c JOIN {Map} m ON m.old_id = c.user_id
            WHERE c.kind = 'discord' AND c.verified = 1 AND c.value {DiscordId} ORDER BY c.connected_at
            """).ConfigureAwait(false);
        var web = 0;
        if (await _db.TableExistsAsync(_settings.OsuWeb, "tomyum_user_connections").ConfigureAwait(false) > 0)
        {
            web = await _db.ExecAsync($"""
                INSERT IGNORE INTO {W}.tomyum_user_connections (user_id, kind, external_id, display_name, data, connected_at, checked_at)
                SELECT m.new_id, c.kind, LEFT(c.value, 32), LEFT(c.display_name, 100), NULL, c.connected_at, NULL
                FROM {S}.user_connections c JOIN {Map} m ON m.old_id = c.user_id
                WHERE c.verified = 1 AND c.value <> '' AND (
                    (c.kind = 'discord' AND EXISTS (SELECT 1 FROM {T}.discord_links d WHERE d.user_id = m.new_id AND {Bin("CAST(d.discord_id AS CHAR)")} = {Bin("c.value")}))
                    OR c.kind = 'osu_bancho')
                """).ConfigureAwait(false);
        }
        await _db.ExecAsync($"UPDATE {W}.phpbb_users pu JOIN {Map} m ON m.new_id = pu.user_id JOIN {S}.user_connections c ON c.user_id = m.old_id AND c.kind = 'website' SET pu.user_website = LEFT(c.value, 200) WHERE c.value <> ''").ConfigureAwait(false);
        await _db.ExecAsync($"UPDATE {W}.phpbb_users pu JOIN {Map} m ON m.new_id = pu.user_id JOIN {S}.user_connections c ON c.user_id = m.old_id AND c.kind = 'twitter' SET pu.user_twitter = LEFT(c.value, 255) WHERE c.value <> ''").ConfigureAwait(false);
        return $"{Db.N(discord)} Discord links, {Db.N(web)} website connections";
    }

    private async Task<string> FriendsAsync()
    {
        var friend = _source.HasRelationshipType ? "IF(r.type = 'friend', 1, 0), IF(r.type = 'block', 1, 0)" : "1, 0";
        var where = _source.HasRelationshipType ? " WHERE r.type IN ('friend', 'block')" : "";
        var n = await _db.ExecAsync($"""
            INSERT IGNORE INTO {W}.phpbb_zebra (user_id, zebra_id, friend, foe)
            SELECT m1.new_id, m2.new_id, {friend} FROM {S}.relationships r JOIN {Map} m1 ON m1.old_id = r.user1 JOIN {Map} m2 ON m2.old_id = r.user2{where}
            """).ConfigureAwait(false);
        return $"{Db.N(n)} relationships";
    }

    private async Task<string> BeatmapsAsync()
    {
        // osu!'s maps that were played or carry a staff status. Private sets come later (--custom-sets):
        // they are uploaded again through the server, which gives them ids in TomYum's custom range.
        var n = await _db.ExecAsync($"""
            INSERT IGNORE INTO {T}.beatmaps (Id, SetId, Md5, Artist, Title, Version, Creator, Status, StatusFrozen, Mode, StarRating, TotalLength, MaxCombo, LastUpdate)
            SELECT b.id, b.set_id, b.md5, LEFT(b.artist, 256), LEFT(b.title, 256), LEFT(b.version, 256), LEFT(b.creator, 64),
                b.status, b.frozen, b.mode, b.diff, b.total_length, b.max_combo, b.last_update
            FROM {S}.maps b
            WHERE NOT ({PrivateMap("b")}) AND b.id > 0 AND b.set_id > 0
              AND (b.frozen = 1 OR b.md5 IN (SELECT DISTINCT s.map_md5 FROM {S}.scores s))
            """).ConfigureAwait(false);

        // The website's copies, so its leaderboards and the nominations find them.
        await using var scope = _services.CreateAsyncScope();
        var tomyum = scope.ServiceProvider.GetRequiredService<TomYumDbContext>();
        var mirror = scope.ServiceProvider.GetRequiredService<OsuWebBeatmapMirror>();
        var beatmaps = await tomyum.Beatmaps.AsNoTracking().ToListAsync().ConfigureAwait(false);
        var mirrored = await mirror.BackfillAsync(beatmaps).ConfigureAwait(false);

        var queued = 0;
        if (_source.HasNominations)
        {
            // A set staff qualified keeps its queue date, so osu-web's modding:rank ranks it when the old server would have.
            queued = await _db.ExecAsync($"""
                UPDATE {W}.osu_beatmapsets bs JOIN (
                    SELECT n.set_id, MAX(n.created_at) AS t FROM {S}.beatmap_nominations n WHERE n.action = 'vote' AND n.to_status = 4 GROUP BY n.set_id
                ) q ON q.set_id = bs.beatmapset_id SET bs.queued_at = q.t WHERE bs.approved = 3
                """).ConfigureAwait(false);
        }
        return $"{Db.N(n)} maps into TomYum, {Db.N(mirrored)} copied to the website, {Db.N(queued)} qualified sets keep their queue date";
    }

    private async Task<string> ScoresAsync()
    {
        // pp only where TomYum would give it: a pass on a ranked/approved map. Elsewhere 0, or a loved
        // map's old pp would keep a later play from ever becoming the best (submission compares pp).
        var n = await _db.ExecAsync($"""
            INSERT IGNORE INTO {T}.scores (Id, UserId, BeatmapId, BeatmapMd5, Mode, ScoreValue, MaxCombo, Count300, Count100, Count50, CountGeki, CountKatu, CountMiss,
                Grade, Mods, Perfect, Pp, PpHeld, Passed, Status, SubmittedAt, FromLazer)
            SELECT s.id, m.new_id, COALESCE(b.id, 0), s.map_md5, s.mode, s.score, s.max_combo, s.n300, s.n100, s.n50, s.ngeki, s.nkatu, s.nmiss,
                LEFT(s.grade, 2), s.mods, s.perfect, IF(s.status > 0 AND b.status IN (2, 3), s.pp, 0), 0, s.status > 0, s.status, s.play_time, 0
            FROM {S}.scores s JOIN {Map} m ON m.old_id = s.userid LEFT JOIN {S}.maps b ON b.md5 = s.map_md5
            """).ConfigureAwait(false);
        var next = await _db.CountAsync($"SELECT COALESCE(MAX(Id), 0) + 1 FROM {T}.scores").ConfigureAwait(false);
        await _db.ExecAsync($"ALTER TABLE {T}.scores AUTO_INCREMENT = {Db.N(next)}").ConfigureAwait(false);
        return $"{Db.N(n)} scores; scores AUTO_INCREMENT {Db.N(next)}";
    }

    private async Task<string> ReplaysAsync()
    {
        if (_settings.DataPath is null)
            return "no --data: no replays copied";
        var osr = Path.Combine(_settings.DataPath, "osr");
        if (!Directory.Exists(osr))
            return $"{osr} doesn't exist: no replays copied";
        Directory.CreateDirectory(_settings.ReplaysPath);
        var ids = await _db.QueryAsync($"SELECT s.Id FROM {T}.scores s JOIN {Map} m ON m.new_id = s.UserId WHERE s.Passed = 1", r => r.GetInt64(0)).ConfigureAwait(false);
        int copied = 0, missing = 0, had = 0;
        foreach (var id in ids)
        {
            var source = Path.Combine(osr, Db.N(id) + ".osr");
            var target = Path.Combine(_settings.ReplaysPath, Db.N(id) + ".osr");
            if (File.Exists(target))
            {
                had++;
                continue;
            }
            if (!File.Exists(source))
            {
                missing++;
                continue;
            }
            File.Copy(source, target);
            copied++;
        }

        // bancho.py's cached .osu files go into TomYum's cache under the same names (<beatmap id>.osu), so the
        // pp rework afterwards doesn't download every played map from osu! again.
        var osuFiles = 0;
        var osu = Path.Combine(_settings.DataPath, "osu");
        if (Directory.Exists(osu))
        {
            Directory.CreateDirectory(_settings.OsuCachePath);
            foreach (var file in Directory.EnumerateFiles(osu, "*.osu"))
            {
                var name = Path.GetFileName(file);
                if (!int.TryParse(Path.GetFileNameWithoutExtension(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id >= 100_000_000)
                    continue; // the old private sets are uploaded again and get new files
                var target = Path.Combine(_settings.OsuCachePath, name);
                if (File.Exists(target))
                    continue;
                File.Copy(file, target);
                osuFiles++;
            }
        }
        return $"{Db.N(copied)} replays copied, {Db.N(had)} already there, {Db.N(missing)} missing; {Db.N(osuFiles)} .osu files into the cache";
    }

    private async Task<string> StatsAsync()
    {
        // Play count and scores are kept; pp and accuracy are placeholders until tools/RecalcPp and the
        // website's processor (the mirror step) recompute them with TomYum's calculators.
        var n = await _db.ExecAsync($"""
            INSERT INTO {T}.user_stats (UserId, Mode, RankedScore, TotalScore, PlayCount, Accuracy, Pp, GlobalRank)
            SELECT m.new_id, st.mode, st.rscore, st.tscore, st.plays, st.acc / 100, st.pp, 0
            FROM {S}.stats st JOIN {Map} m ON m.old_id = st.id WHERE st.mode <> 7 AND (st.plays > 0 OR st.pp > 0)
            ON DUPLICATE KEY UPDATE RankedScore = GREATEST(RankedScore, VALUES(RankedScore)), TotalScore = GREATEST(TotalScore, VALUES(TotalScore)), PlayCount = GREATEST(PlayCount, VALUES(PlayCount))
            """).ConfigureAwait(false);
        return $"{Db.N(n)} stats rows";
    }

    private async Task<string> AchievementsAsync()
    {
        // The unlock date: bancho.py only started recording it in mid-2026 (unlocked_at); without one the
        // player's sign-up date, old enough that TomYum's Discord medal feed never posts it.
        var at = _source.HasUnlockedAt ? "COALESCE(ua.unlocked_at, FROM_UNIXTIME(GREATEST(u.creation_time, 1)))" : "FROM_UNIXTIME(GREATEST(u.creation_time, 1))";
        var from = $"FROM {S}.user_achievements ua JOIN {S}.achievements a ON a.id = ua.achid JOIN {W}.osu_achievements oa ON {Bin("oa.slug")} = {Bin("a.file")} JOIN {Map} m ON m.old_id = ua.userid JOIN {S}.users u ON u.id = ua.userid";
        var n = await _db.ExecAsync($"INSERT IGNORE INTO {T}.user_achievements (UserId, AchievementId, UnlockedAt) SELECT m.new_id, oa.achievement_id, {at} {from}").ConfigureAwait(false);
        await _db.ExecAsync($"INSERT IGNORE INTO {W}.osu_user_achievements (user_id, achievement_id, `date`, beatmap_id) SELECT m.new_id, oa.achievement_id, {at}, NULL {from}").ConfigureAwait(false);
        await _db.ExecAsync($"UPDATE {W}.osu_achievements a SET a.achieved_count = (SELECT COUNT(*) FROM {W}.osu_user_achievements ua WHERE ua.achievement_id = a.achievement_id)").ConfigureAwait(false);
        var custom = await _db.CountAsync($"SELECT COUNT(*) FROM {S}.achievements a WHERE NOT EXISTS (SELECT 1 FROM {W}.osu_achievements oa WHERE {Bin("oa.slug")} = {Bin("a.file")})").ConfigureAwait(false);
        return $"{Db.N(n)} medal unlocks; {Db.N(custom)} of the old server's own medals are left to tomyum:custom-medals-import";
    }

    private async Task<string> RankHistoryAsync()
    {
        if (!_source.HasRankingHistory)
            return "no ranking_history table: nothing to do";

        // osu-web keeps 90 days per player and ruleset in a ring of columns r0..r89; osu_counts says which
        // column was written last (C) and TomYum's OsuWebRankHistory which day that was (T). A day k days
        // before T is column (C - k) mod 90, and only k = 1..89 shows on the graph. On a fresh website the
        // ring is started here so that TomYum's first daily record lands on column 0.
        var today = DateTime.UtcNow.Date;
        var doneDay = await _db.ScalarAsync<long?>($"SELECT count FROM {W}.osu_counts WHERE name = 'tomyum_rank_history_day'").ConfigureAwait(false);
        DateTime t;
        if (doneDay is { } d && DateTime.TryParseExact(Db.N(d), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            t = parsed.Date;
        }
        else
        {
            t = today.AddDays(-1);
            await _db.ExecAsync($"INSERT INTO {W}.osu_counts (name, count) VALUES ('tomyum_rank_history_day', {Db.N(long.Parse(t.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture))}) ON DUPLICATE KEY UPDATE count = VALUES(count)").ConfigureAwait(false);
        }

        var written = 0;
        for (var mode = 0; mode < 4; mode++)
        {
            var column = await _db.ScalarAsync<long?>($"SELECT count FROM {W}.osu_counts WHERE name = @n", ("@n", $"pp_rank_column_{OsuWebRankHistory.Rulesets[mode]}")).ConfigureAwait(false);
            if (column is null)
            {
                column = 89;
                await _db.ExecAsync($"INSERT INTO {W}.osu_counts (name, count) VALUES (@n, 89) ON DUPLICATE KEY UPDATE count = VALUES(count)", ("@n", $"pp_rank_column_{OsuWebRankHistory.Rulesets[mode]}")).ConfigureAwait(false);
            }
            var c = (int)(column.Value % 90);

            await _db.ExecAsync($"""
                INSERT IGNORE INTO {W}.osu_user_performance_rank (user_id, mode)
                SELECT DISTINCT m.new_id, {Db.N(mode)} FROM {S}.ranking_history rh JOIN {Map} m ON m.old_id = rh.user_id WHERE rh.mode = {Db.N(mode)} AND rh.global_rank > 0
                """).ConfigureAwait(false);
            for (var k = 1; k <= 89; k++)
            {
                var day = t.AddDays(-k);
                var r = "r" + Db.N(((c - k) % 90 + 90) % 90);
                written += await _db.ExecAsync($"""
                    UPDATE {W}.osu_user_performance_rank pr JOIN (
                        SELECT m.new_id AS user_id, rh.global_rank FROM {S}.ranking_history rh JOIN {Map} m ON m.old_id = rh.user_id
                        JOIN (SELECT user_id, MAX(timestamp) AS ts FROM {S}.ranking_history WHERE mode = {Db.N(mode)} AND DATE(timestamp) = @d GROUP BY user_id) last
                          ON last.user_id = rh.user_id AND last.ts = rh.timestamp
                        WHERE rh.mode = {Db.N(mode)} AND rh.global_rank > 0
                    ) x ON x.user_id = pr.user_id SET pr.{r} = x.global_rank WHERE pr.mode = {Db.N(mode)}
                    """, ("@d", day)).ConfigureAwait(false);
            }
            await _db.ExecAsync($"""
                INSERT INTO {W}.osu_user_performance_rank_highest (user_id, mode, `rank`, updated_at)
                SELECT m.new_id, {Db.N(mode)}, best.r, best.t FROM (
                    SELECT rh.user_id, MIN(rh.global_rank) AS r, MIN(rh.timestamp) AS t FROM {S}.ranking_history rh
                    WHERE rh.mode = {Db.N(mode)} AND rh.global_rank > 0 GROUP BY rh.user_id
                ) best JOIN {Map} m ON m.old_id = best.user_id
                ON DUPLICATE KEY UPDATE updated_at = IF(VALUES(`rank`) < `rank`, VALUES(updated_at), updated_at), `rank` = LEAST(`rank`, VALUES(`rank`))
                """).ConfigureAwait(false);
        }

        // Relax and autopilot ranks live in TomYum's own history.
        var modes = await _db.ExecAsync($"""
            INSERT IGNORE INTO {T}.mode_rank_history (UserId, Mode, Day, `Rank`)
            SELECT m.new_id, rh.mode, DATE(rh.timestamp), MIN(rh.global_rank) FROM {S}.ranking_history rh JOIN {Map} m ON m.old_id = rh.user_id
            WHERE rh.mode >= 4 AND rh.global_rank > 0 AND rh.timestamp >= UTC_TIMESTAMP() - INTERVAL 90 DAY GROUP BY m.new_id, rh.mode, DATE(rh.timestamp)
            """).ConfigureAwait(false);
        return $"{Db.N(written)} website rank days (T = {t:yyyy-MM-dd}), {Db.N(modes)} relax/autopilot rank days";
    }

    private async Task<string> FavouritesAsync()
    {
        var favourites = 0;
        if (_source.HasFavourites)
        {
            favourites = await _db.ExecAsync($"""
                INSERT IGNORE INTO {W}.osu_favouritemaps (user_id, beatmapset_id, dateadded)
                SELECT m.new_id, f.setid, FROM_UNIXTIME(GREATEST(f.created_at, 1)) FROM {S}.favourites f JOIN {Map} m ON m.old_id = f.userid
                JOIN {W}.osu_beatmapsets bs ON bs.beatmapset_id = f.setid
                """).ConfigureAwait(false);
            await _db.ExecAsync($"UPDATE {W}.osu_beatmapsets bs SET bs.favourite_count = (SELECT COUNT(*) FROM {W}.osu_favouritemaps f WHERE f.beatmapset_id = bs.beatmapset_id) WHERE EXISTS (SELECT 1 FROM {W}.osu_favouritemaps f WHERE f.beatmapset_id = bs.beatmapset_id)").ConfigureAwait(false);
        }
        var ratings = 0;
        if (_source.HasRatings)
        {
            ratings = await _db.ExecAsync($"""
                INSERT IGNORE INTO {W}.osu_user_beatmapset_ratings (user_id, beatmapset_id, rating, `date`)
                SELECT m.new_id, b.set_id, GREATEST(1, LEAST(10, r.rating)), UTC_TIMESTAMP() FROM {S}.ratings r JOIN {Map} m ON m.old_id = r.userid
                JOIN {S}.maps b ON b.md5 = r.map_md5 JOIN {W}.osu_beatmapsets bs ON bs.beatmapset_id = b.set_id
                """).ConfigureAwait(false);
        }
        return $"{Db.N(favourites)} favourites, {Db.N(ratings)} ratings";
    }

    private async Task<string> HashesAsync()
    {
        // TomYum's own table keeps the multi-account checks; osu-web's trusts each player's known
        // machines, so nobody is asked to verify a PC they already played on. osu-web stores md5s as bytes.
        var n = await _db.ExecAsync($"""
            INSERT INTO {T}.client_hashes (user_id, osupath, adapters, uninstall_id, disk_serial, latest_time, occurrences)
            SELECT m.new_id, LOWER(TRIM(h.osupath)), LOWER(TRIM(h.adapters)), LOWER(TRIM(h.uninstall_id)), LOWER(TRIM(h.disk_serial)), h.latest_time, h.occurrences
            FROM {S}.client_hashes h JOIN {Map} m ON m.old_id = h.userid
            ON DUPLICATE KEY UPDATE occurrences = GREATEST(client_hashes.occurrences, VALUES(occurrences)), latest_time = GREATEST(client_hashes.latest_time, VALUES(latest_time))
            """).ConfigureAwait(false);
        const string Md5 = "REGEXP '^[0-9a-fA-F]{32}$'";
        var trusted = await _db.ExecAsync($"""
            INSERT IGNORE INTO {W}.osu_user_security (user_id, osu_md5, unique_md5, disk_md5, mac_md5, timestamp, verified)
            SELECT m.new_id, UNHEX(TRIM(h.osupath)), UNHEX(TRIM(h.uninstall_id)), UNHEX(TRIM(h.disk_serial)),
                IF(TRIM(h.adapters) {Md5}, UNHEX(TRIM(h.adapters)), UNHEX(REPEAT('0', 32))), h.latest_time, 1
            FROM {S}.client_hashes h JOIN {Map} m ON m.old_id = h.userid
            WHERE TRIM(h.osupath) {Md5} AND TRIM(h.uninstall_id) {Md5} AND TRIM(h.disk_serial) {Md5}
            ORDER BY h.latest_time
            """).ConfigureAwait(false);
        return $"{Db.N(n)} device rows, {Db.N(trusted)} trusted machines on the website";
    }

    private async Task<string> StaffLogAsync()
    {
        // Prefixed bpy. so they stand apart from TomYum's own actions. The old bot's rows are BanchoBot's.
        var n = await _db.ExecAsync($"""
            INSERT INTO {T}.staff_log (CreatedAt, ActorId, Action, TargetId, Summary, Result, JobId, Via)
            SELECT l.time, COALESCE(ma.new_id, {Db.N(_settings.BotUserId)}), CONCAT('bpy.', LEFT(l.action, 60)), mt.new_id, LEFT(COALESCE(l.msg, ''), 512), NULL, NULL, 'bancho.py'
            FROM {S}.logs l LEFT JOIN {Map} ma ON ma.old_id = l.`from` LEFT JOIN {Map} mt ON mt.old_id = l.`to`
            ORDER BY l.time, l.id
            """).ConfigureAwait(false);
        return $"{Db.N(n)} staff log rows";
    }

    private async Task<string> NominationsAsync()
    {
        if (!_source.HasNominations)
            return "no beatmap_nominations table: nothing to do";

        var rows = await _db.QueryAsync($"""
            SELECT n.set_id, m.new_id, n.action, n.from_status, n.to_status, n.created_at
            FROM {S}.beatmap_nominations n LEFT JOIN {Map} m ON m.old_id = n.actor_id
            JOIN {W}.osu_beatmapsets bs ON bs.beatmapset_id = n.set_id
            ORDER BY n.created_at, n.id
            """, r => (SetId: r.GetInt32(0), UserId: r.IsDBNull(1) ? (int?)null : r.GetInt32(1), Action: r.GetString(2),
                From: r.IsDBNull(3) ? (int?)null : r.GetInt32(3), To: r.GetInt32(4), At: r.GetDateTime(5))).ConfigureAwait(false);

        var events = 0;
        foreach (var row in rows)
        {
            var types = (row.Action, row.From, row.To) switch
            {
                ("vote", _, 4) => new[] { "nominate", "qualify" },
                ("vote", _, _) => ["nominate"],
                ("rank", _, _) => ["rank"],
                ("love", _, _) => ["love"],
                ("cancel", 5, _) => ["remove_from_loved"],
                ("cancel", _, _) => ["disqualify"],
                _ => [],
            };
            foreach (var type in types)
            {
                // Direct ranks by the old bot were its auto-ranks: system events, like osu!'s own.
                int? userId = type == "rank" && row.UserId is null ? null : row.UserId;
                var comment = type == "nominate"
                    ? await _db.ScalarAsync<string>($"SELECT JSON_OBJECT('modes', JSON_ARRAYAGG(d.m)) FROM (SELECT DISTINCT ELT(b.playmode + 1, 'osu', 'taiko', 'fruits', 'mania') AS m FROM {W}.osu_beatmaps b WHERE b.beatmapset_id = {Db.N(row.SetId)}) d").ConfigureAwait(false)
                    : null;
                await _db.ExecAsync($"INSERT INTO {W}.beatmapset_events (beatmapset_id, user_id, type, comment, created_at, updated_at) VALUES (@s, @u, @t, @c, @at, @at)",
                    ("@s", row.SetId), ("@u", userId), ("@t", type), ("@c", comment), ("@at", row.At)).ConfigureAwait(false);
                events++;
                if (type == "nominate" && userId is not null)
                {
                    var eventId = await _db.ScalarAsync<long>("SELECT LAST_INSERT_ID()").ConfigureAwait(false);
                    await _db.ExecAsync($"""
                        INSERT INTO {W}.beatmapset_nominations (beatmapset_id, user_id, modes, event_id, created_at, updated_at)
                        SELECT @s, @u, JSON_OBJECTAGG(ELT(b.playmode + 1, 'osu', 'taiko', 'fruits', 'mania'), 'full'), @e, @at, @at
                        FROM (SELECT DISTINCT playmode FROM {W}.osu_beatmaps WHERE beatmapset_id = @s) b
                        """, ("@s", row.SetId), ("@u", userId), ("@e", eventId), ("@at", row.At)).ConfigureAwait(false);
                }
            }
        }
        return $"{Db.N(events)} nomination events for {Db.N(rows.Select(r => r.SetId).Distinct().Count())} sets";
    }

    /// <summary>
    /// The website's copies: every vanilla score (CopyHistoryAsync writes them, the players' stats and
    /// the global ranks), the play counts, which copies have a replay, and the queues (osu-web's score
    /// indexer; ppy's score processor for pp, when the server uses it).
    /// </summary>
    public async Task<string> MirrorAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var tomyum = scope.ServiceProvider.GetRequiredService<TomYumDbContext>();
        var osuWeb = scope.ServiceProvider.GetRequiredService<OsuWebDbContext>();
        var mirror = new OsuWebScoreMirror(tomyum, osuWeb, new OsuWebMirrorOptions { ScoreProcessor = _settings.ScoreProcessor });

        var progress = new Progress<int>(n => Console.Write($"\r  website scores: {Db.N(n)} copied..."));
        var copied = await mirror.CopyHistoryAsync(progress).ConfigureAwait(false);
        Console.WriteLine();
        var plays = await mirror.RebuildPlayCountsAsync().ConfigureAwait(false);
        var replays = Directory.Exists(_settings.ReplaysPath)
            ? await mirror.MarkReplaysAsync(id => File.Exists(Path.Combine(_settings.ReplaysPath, Db.N(id) + ".osr"))).ConfigureAwait(false)
            : 0;

        // The website's "last played" comes from the import otherwise (the mirror writes now): the real
        // dates, so the rankings' inactivity rule and the profiles' "last active" are right.
        for (var mode = 0; mode < 4; mode++)
        {
            await _db.ExecAsync($"""
                UPDATE {W}.{OsuWebScores.StatsTable(mode)} w
                JOIN (SELECT UserId, MAX(SubmittedAt) AS t FROM {T}.scores WHERE Mode = {Db.N(mode)} GROUP BY UserId) m ON m.UserId = w.user_id
                SET w.last_played = m.t
                """).ConfigureAwait(false);
        }

        var queued = await QueueAsync(osuWeb, copied).ConfigureAwait(false);
        return $"{Db.N(copied.Count)} website scores, {Db.N(plays)} play-count rows, {Db.N(replays)} replays marked, last-played dates set; {queued}";
    }

    /// <summary>Queues website scores for the search index and (when on) ppy's processor.</summary>
    public async Task<string> QueueAsync(OsuWebDbContext osuWeb, IReadOnlyList<ulong> scoreIds)
    {
        if (scoreIds.Count == 0)
            return "nothing to queue";
        var redisConfig = _settings.RedisConfiguration;
        try
        {
            await using var redis = await ConnectionMultiplexer.ConnectAsync(redisConfig).ConfigureAwait(false);
            var indexing = new OsuWebScoreIndexing(redis, _settings.IndexPrefix);
            foreach (var batch in scoreIds.Chunk(1000))
                await indexing.QueueAsync(batch).ConfigureAwait(false);
            var processed = 0;
            if (_settings.ScoreProcessor)
            {
                var processing = new OsuWebScoreProcessing(redis);
                foreach (var batch in scoreIds.Chunk(500))
                {
                    var rows = await osuWeb.Scores.AsNoTracking().Where(s => batch.Contains(s.Id)).ToListAsync().ConfigureAwait(false);
                    await processing.QueueAsync(rows.Select(r => OsuWebScores.ProcessingItem(r)).ToList()).ConfigureAwait(false);
                    processed += rows.Count;
                }
            }
            return $"{Db.N(scoreIds.Count)} queued for the search index" + (_settings.ScoreProcessor ? $", {Db.N(processed)} for the score processor" : "");
        }
        catch (RedisConnectionException ex)
        {
            Say($"  Redis at {redisConfig} couldn't be reached ({ex.Message}): the website scores aren't in its search index yet (README: queueing them later).");
            return "Redis unreachable: nothing queued";
        }
    }

    /// <summary>The id map as (old, new) pairs, for the custom-set step and the media command.</summary>
    public async Task<Dictionary<int, int>> UserMapAsync() =>
        (await _db.QueryAsync($"SELECT old_id, new_id FROM {Map}", r => (Old: r.GetInt32(0), New: r.GetInt32(1))).ConfigureAwait(false))
        .ToDictionary(p => p.Old, p => p.New);

    public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Appends a csv line (values quoted).</summary>
    public static string CsvLine(params object?[] values)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(values[i] switch
            {
                null => "",
                string s => Csv(s),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                var o => Csv(o.ToString() ?? ""),
            });
        }
        return sb.ToString();
    }

    /// <summary>Reads a MySQL row value that may be null.</summary>
    public static string? NullableString(MySqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
