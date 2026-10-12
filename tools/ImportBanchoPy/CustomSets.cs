using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Tools.ImportBanchoPy;

/// <summary>
/// The old server's own (private) beatmap sets. bancho.py numbered them from 100,000,000, which is
/// past osu-web's id columns, so each <c>.osz</c> is uploaded again through the running server
/// (<c>POST /web/tomyum/beatmapset-upload</c>, the BN panel's upload, with a ticket signed by the
/// InterOp secret), which gives it an id in TomYum's custom range and rewrites the ids in its
/// <c>.osu</c> files. Then the scores on them get the new map ids and md5s, the old statuses are
/// put back (frozen), and the scores are copied to the website.
/// </summary>
internal sealed class CustomSets
{
    private readonly Db _db;
    private readonly SourceSchema _source;
    private readonly ImportSettings _settings;
    private readonly Importer _importer;
    private readonly IServiceProvider _services;
    private readonly StreamWriter _log;

    private readonly string S;
    private readonly string T;
    private readonly string W;

    public CustomSets(Db db, SourceSchema source, ImportSettings settings, Importer importer, IServiceProvider services, StreamWriter log)
    {
        _db = db;
        _source = source;
        _settings = settings;
        _importer = importer;
        _services = services;
        _log = log;
        S = Db.Identifier(source.Schema);
        T = Db.Identifier(settings.TomYum);
        W = Db.Identifier(settings.OsuWeb);
    }

    private void Say(string line)
    {
        Console.WriteLine(line);
        _log.WriteLine(line);
    }

    /// <summary>The upload ticket: hex HMAC-SHA256 over <c>upload\n{user}\n{set}\n{expires}</c> (set empty for a new set), as the server checks it.</summary>
    public static string Ticket(string secret, int userId, long expires)
    {
        var message = string.Create(CultureInfo.InvariantCulture, $"upload\n{userId}\n\n{expires}");
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message)));
    }

    public async Task<string> RunAsync()
    {
        if (_settings.DataPath is null)
            throw new InvalidOperationException("--custom-sets needs --data (bancho.py's .data folder with osz/).");
        if (_settings.ServerUrl is null || string.IsNullOrWhiteSpace(_settings.Domain) || string.IsNullOrWhiteSpace(_settings.InteropSecret))
            throw new InvalidOperationException("--custom-sets needs the running server: --server-url (e.g. http://server:8080), the domain (Server:Domain or --domain) and OsuWeb:InteropSecret (TOMYUM_OSUWEB__INTEROPSECRET).");

        var map = await _importer.UserMapAsync().ConfigureAwait(false);
        var isPrivate = _source.HasMapServer ? "(b.server = 'private' OR b.id >= 100000000)" : "b.id >= 100000000";
        var owner = _source.HasMapOwner ? "b.owner_id" : "NULL";
        var uploader = _source.HasMapsetUploader ? "(SELECT ms.uploaded_by FROM " + S + ".mapsets ms WHERE ms.id = b.set_id)" : "NULL";
        var diffs = await _db.QueryAsync($"""
            SELECT b.set_id, b.id, b.md5, b.version, b.status, b.frozen, {owner}, {uploader}
            FROM {S}.maps b WHERE {isPrivate} ORDER BY b.set_id, b.id
            """, r => new OldDiff(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetBoolean(5),
                r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7))).ConfigureAwait(false);

        var csv = new List<string> { "old_set_id,new_set_id,old_beatmap_id,new_beatmap_id,version,old_md5,new_md5" };
        using var http = new HttpClient { BaseAddress = _settings.ServerUrl, Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Host = $"osu.{_settings.Domain}";

        int uploaded = 0, skipped = 0, remappedScores = 0;
        var newScoreIds = new List<ulong>();
        foreach (var set in diffs.GroupBy(d => d.SetId))
        {
            var osz = Path.Combine(_settings.DataPath, "osz", Db.N(set.Key) + ".osz");
            if (!File.Exists(osz))
            {
                Say($"  set {Db.N(set.Key)}: no {osz}, skipped");
                skipped++;
                continue;
            }
            if (await _db.CountAsync($"SELECT COUNT(*) FROM {T}.{"bpy_import_custom_sets"} WHERE old_set_id = {Db.N(set.Key)}").ConfigureAwait(false) > 0)
            {
                Say($"  set {Db.N(set.Key)}: uploaded by an earlier run, skipped");
                continue;
            }

            // Whoever uploaded it on the old server, if they are a player here; else BanchoBot.
            var oldOwner = set.Select(d => d.Owner ?? d.Uploader).FirstOrDefault(o => o is not null);
            var uploaderId = oldOwner is { } o && map.TryGetValue(o, out var mapped) ? mapped : _settings.BotUserId;

            // A set this run uploaded before it could record it (a crash in between): reuse it instead of uploading twice.
            var first = set.First();
            var meta = await _db.QueryAsync($"SELECT artist, title, creator FROM {S}.maps WHERE id = {Db.N(first.Id)}",
                r => (Artist: r.GetString(0), Title: r.GetString(1), Creator: r.GetString(2))).ConfigureAwait(false);
            var existing = meta.Count == 0 ? null : await _db.ScalarAsync<int?>($"""
                SELECT MIN(b.SetId) FROM {T}.beatmaps b WHERE b.Id >= 10000000 AND b.Artist = @a AND b.Title = @t AND b.Creator = @c
                  AND b.SetId NOT IN (SELECT new_set_id FROM {T}.bpy_import_custom_sets)
                """, ("@a", meta[0].Artist), ("@t", meta[0].Title), ("@c", meta[0].Creator)).ConfigureAwait(false);
            int newSetId;
            List<(int Id, string Version)> newDiffs;
            if (existing is { } reused)
            {
                newSetId = reused;
                newDiffs = await _db.QueryAsync($"SELECT Id, Version FROM {T}.beatmaps WHERE SetId = {Db.N(reused)}", r => (r.GetInt32(0), r.GetString(1))).ConfigureAwait(false);
                Say($"  set {Db.N(set.Key)}: already uploaded as {Db.N(reused)} (not recorded before), reusing it");
            }
            else
            {
                var expires = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
                var query = string.Create(CultureInfo.InvariantCulture, $"/web/tomyum/beatmapset-upload?user={uploaderId}&expires={expires}&signature={Ticket(_settings.InteropSecret, uploaderId, expires)}");
                using var form = new MultipartFormDataContent();
                var bytes = await File.ReadAllBytesAsync(osz).ConfigureAwait(false);
                using var file = new ByteArrayContent(bytes);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(file, "osz", Db.N(set.Key) + ".osz");
                using var response = await http.PostAsync(new Uri(query, UriKind.Relative), form).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Say($"  set {Db.N(set.Key)}: the server said {(int)response.StatusCode}: {body}");
                    skipped++;
                    continue;
                }

                using var json = JsonDocument.Parse(body);
                newSetId = json.RootElement.GetProperty("set_id").GetInt32();
                // The server answers with camelCase keys (set_id, beatmap_id, version, mode, star_rating).
                newDiffs = json.RootElement.GetProperty("difficulties").EnumerateArray()
                    .Select(d => (Id: d.GetProperty("beatmap_id").GetInt32(), Version: (d.TryGetProperty("version", out var v) ? v : d.GetProperty("Version")).GetString() ?? ""))
                    .ToList();
                uploaded++;
            }
            await _db.ExecAsync($"INSERT INTO {T}.bpy_import_custom_sets (old_set_id, new_set_id, uploaded_at) VALUES ({Db.N(set.Key)}, {Db.N(newSetId)}, UTC_TIMESTAMP(6))").ConfigureAwait(false);

            var setStatuses = new List<int>();
            foreach (var old in set)
            {
                var match = newDiffs.FirstOrDefault(d => string.Equals(d.Version, old.Version, StringComparison.Ordinal));
                if (match.Id == 0)
                    match = newDiffs.FirstOrDefault(d => string.Equals(d.Version.Trim(), old.Version.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match.Id == 0)
                {
                    Say($"  set {Db.N(set.Key)} -> {Db.N(newSetId)}: difficulty [{old.Version}] isn't in the upload; its scores stay on the old id");
                    csv.Add(Importer.CsvLine(set.Key, newSetId, old.Id, null, old.Version, old.Md5, null));
                    continue;
                }
                var newMd5 = await _db.ScalarAsync<string>($"SELECT Md5 FROM {T}.beatmaps WHERE Id = {Db.N(match.Id)}").ConfigureAwait(false) ?? "";
                remappedScores += await _db.ExecAsync($"UPDATE {T}.scores SET BeatmapId = {Db.N(match.Id)}, BeatmapMd5 = @md5 WHERE BeatmapMd5 = @old", ("@md5", newMd5), ("@old", old.Md5)).ConfigureAwait(false);

                // The old status back, held against osu! API refreshes (loved sets stay loved, pending stay pending).
                if (old.Frozen || old.Status is 2 or 3 or 5)
                {
                    await _db.ExecAsync($"UPDATE {T}.beatmaps SET Status = {Db.N(old.Status)}, StatusFrozen = 1 WHERE Id = {Db.N(match.Id)}").ConfigureAwait(false);
                    await _db.ExecAsync($"UPDATE {W}.osu_beatmaps SET approved = {Db.N(Approved(old.Status))} WHERE beatmap_id = {Db.N(match.Id)}").ConfigureAwait(false);
                }
                setStatuses.Add(old.Frozen || old.Status is 2 or 3 or 5 ? Approved(old.Status) : 0);
                csv.Add(Importer.CsvLine(set.Key, newSetId, old.Id, match.Id, old.Version, old.Md5, newMd5));
            }
            if (setStatuses.Count > 0)
            {
                var best = OsuWebBeatmaps.SetStatusFor(setStatuses.Select(s => (sbyte)s).ToList());
                await _db.ExecAsync($"UPDATE {W}.osu_beatmapsets SET approved = {Db.N(best)}, approved_date = IF({Db.N(best)} > 0, COALESCE(approved_date, UTC_TIMESTAMP()), NULL) WHERE beatmapset_id = {Db.N(newSetId)}").ConfigureAwait(false);
            }
            Say($"  set {Db.N(set.Key)} -> {Db.N(newSetId)} ({Db.N(newDiffs.Count)} difficulties)");
        }

        await File.WriteAllLinesAsync(Path.Combine(_settings.OutDir, "custom-set-ids.csv"), csv).ConfigureAwait(false);

        // The scores that now point at maps the website has: copy them and queue them.
        if (remappedScores > 0)
        {
            await using var scope = _services.CreateAsyncScope();
            var tomyum = scope.ServiceProvider.GetRequiredService<TomYum.Persistence.TomYumDbContext>();
            var osuWeb = scope.ServiceProvider.GetRequiredService<OsuWebDbContext>();
            var mirror = new OsuWebScoreMirror(tomyum, osuWeb, new OsuWebMirrorOptions { ScoreProcessor = _settings.ScoreProcessor });
            newScoreIds.AddRange(await mirror.CopyHistoryAsync().ConfigureAwait(false));
            await mirror.RebuildPlayCountsAsync().ConfigureAwait(false);
            if (Directory.Exists(_settings.ReplaysPath))
                await mirror.MarkReplaysAsync(id => File.Exists(Path.Combine(_settings.ReplaysPath, Db.N(id) + ".osr"))).ConfigureAwait(false);
            Say("  " + await _importer.QueueAsync(osuWeb, newScoreIds).ConfigureAwait(false));
        }
        return $"{Db.N(uploaded)} sets uploaded, {Db.N(skipped)} skipped, {Db.N(remappedScores)} scores re-pointed, {Db.N(newScoreIds.Count)} copied to the website. Restart the server so it holds the statuses (it reads frozen ones at start).";
    }

    /// <summary>
    /// Starts a pp rework of every mode on the running server (<c>POST /_admin/pp-reworks</c>, signed like the
    /// website's panels sign admin calls): the old server's pp came from another calculator, so every
    /// passed score is worked out again with TomYum's, then bests, totals, the website's copies (through
    /// ppy's processor) and ranks follow; the rankings are paused meanwhile. Returns the rework's id.
    /// </summary>
    public async Task<string> StartReworkAsync()
    {
        if (_settings.ServerUrl is null || string.IsNullOrWhiteSpace(_settings.Domain) || string.IsNullOrWhiteSpace(_settings.InteropSecret))
            throw new InvalidOperationException("--rework needs the running server: --server-url, the domain (Server:Domain or --domain) and OsuWeb:InteropSecret.");

        var modes = await _db.QueryAsync($"SELECT DISTINCT Mode FROM {T}.scores WHERE Passed = 1 ORDER BY Mode", r => r.GetInt32(0)).ConfigureAwait(false);
        if (modes.Count == 0)
            return "no passed scores: no rework";
        var body = JsonSerializer.SerializeToUtf8Bytes(new { modes, library = (string?)null, fromVersion = (string?)null, toVersion = (string?)null, reason = "Imported from bancho.py: every score's pp with this server's calculators." });
        var pathAndQuery = string.Create(CultureInfo.InvariantCulture, $"/_admin/pp-reworks?timestamp={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        var actor = Db.N(_settings.BotUserId);
        var head = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"POST\n{pathAndQuery}\n{actor}\n"));
        var message = new byte[head.Length + body.Length];
        head.CopyTo(message, 0);
        body.CopyTo(message, head.Length);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_settings.InteropSecret), message));

        using var http = new HttpClient { BaseAddress = _settings.ServerUrl, Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Host = "server";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(pathAndQuery, UriKind.Relative));
        request.Headers.Add("X-TomYum-Signature", signature);
        request.Headers.Add("X-Actor-Id", actor);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await http.SendAsync(request).ConfigureAwait(false);
        var answer = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The server refused the pp rework ({(int)response.StatusCode}): {answer}");
        return $"pp rework started for modes {string.Join(", ", modes.Select(m => Db.N(m)))}: {answer}. The admin panel (Admin → pp rework) shows its progress; rankings are paused until it is done.";
    }

    /// <summary>
    /// osu!dan (the somtum fork marks dan maps with <c>maps.is_dan</c>; each dan is one difficulty): every
    /// such map becomes a course of the <c>tomyum.dan</c> plugin (name = the difficulty, category = the
    /// set's title, one map), and each player's best passed score that meets the course's rules (accuracy
    /// 96%, 95% for an intro dan; the allowed mods) becomes their clear. The old server kept no clear
    /// records (it only posted them to Discord), so clears are read off the scores. Needs the plugin's
    /// tables (the server has run with the plugin once) and the sets uploaded by <see cref="RunAsync"/>.
    /// </summary>
    public async Task<string> ImportDanAsync()
    {
        if (await _db.ColumnExistsAsync(_source.Schema, "maps", "is_dan").ConfigureAwait(false) == 0)
            return "the old server has no maps.is_dan: nothing to do";
        if (await _db.TableExistsAsync(_settings.TomYum, "plugin_dan_courses").ConfigureAwait(false) == 0)
            return "no plugin_dan_courses table: start the server with the tomyum.dan plugin once, then run --dan again";

        // Old dan map -> the uploaded copy (same set through bpy_import_custom_sets, same difficulty name).
        var old = await _db.QueryAsync($"""
            SELECT b.set_id, b.id, b.version, b.title, COALESCE(cs.new_set_id, 0)
            FROM {S}.maps b LEFT JOIN {T}.bpy_import_custom_sets cs ON cs.old_set_id = b.set_id
            WHERE b.is_dan = 1 ORDER BY b.set_id, b.id
            """, r => (SetId: r.GetInt32(0), Id: r.GetInt32(1), Version: r.GetString(2), Title: r.GetString(3), NewSetId: r.GetInt32(4))).ConfigureAwait(false);
        int courses = 0, skipped = 0, clears = 0, position = 0, lastSet = 0;
        foreach (var map in old)
        {
            position = map.SetId == lastSet ? position + 1 : 1;
            lastSet = map.SetId;
            var newId = map.NewSetId == 0
                ? (map.Id < 10_000_000 ? map.Id : 0) // an osu! map marked as a dan keeps its id
                : await _db.ScalarAsync<int?>($"SELECT Id FROM {T}.beatmaps WHERE SetId = {Db.N(map.NewSetId)} AND Version = @v", ("@v", map.Version)).ConfigureAwait(false) ?? 0;
            if (newId == 0 || await _db.CountAsync($"SELECT COUNT(*) FROM {T}.beatmaps WHERE Id = {Db.N(newId)}").ConfigureAwait(false) == 0)
            {
                Say($"  dan [{map.Version}] of set {Db.N(map.SetId)}: its map isn't here (upload the private sets first), skipped");
                skipped++;
                continue;
            }
            if (await _db.CountAsync($"SELECT COUNT(*) FROM {T}.plugin_dan_course_maps WHERE beatmap_id = {Db.N(newId)}").ConfigureAwait(false) > 0)
                continue; // imported already

            var isIntro = System.Text.RegularExpressions.Regex.IsMatch(map.Version, @"\b(intro|1st(?:\s*dan)?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var keys = System.Text.RegularExpressions.Regex.Match(map.Title + " " + map.Version, @"\b([4-9]|10)\s*[Kk]\b") is { Success: true } k ? int.Parse(k.Groups[1].Value, CultureInfo.InvariantCulture) : 4;
            var minAccuracy = isIntro ? 95.00m : 96.00m;
            const uint AllowedMods = 1 << 30 | 8 | 1 << 20 | 1024 | 16 | 64 | 512; // Mirror, Hidden, FadeIn, Flashlight, HardRock, DoubleTime, Nightcore
            await _db.ExecAsync($"""
                INSERT INTO {T}.plugin_dan_courses (name, category, description, mode, `keys`, sort_order, is_intro, min_accuracy, allowed_mods, no_pause, active, created_by)
                VALUES (@n, @c, 'Imported from the previous server.', 3, {Db.N(keys)}, {Db.N(position)}, {(isIntro ? 1 : 0)}, {minAccuracy.ToString(CultureInfo.InvariantCulture)}, {Db.N(AllowedMods)}, 1, 1, NULL)
                """, ("@n", map.Version.Length > 100 ? map.Version[..100] : map.Version), ("@c", map.Title.Length > 32 ? map.Title[..32] : map.Title)).ConfigureAwait(false);
            var courseId = await _db.ScalarAsync<long>("SELECT LAST_INSERT_ID()").ConfigureAwait(false);
            await _db.ExecAsync($"INSERT INTO {T}.plugin_dan_course_maps (course_id, position, beatmap_id) VALUES ({Db.N(courseId)}, 1, {Db.N(newId)})").ConfigureAwait(false);
            courses++;

            // Clears: each player's best qualifying pass (accuracy from the old scores table, same ids), oldest such pass as the date.
            clears += await _db.ExecAsync($"""
                INSERT IGNORE INTO {T}.plugin_dan_clears (user_id, course_id, score_id, accuracy, mods, cleared_at)
                SELECT q.UserId, {Db.N(courseId)}, q.Id, q.acc, q.Mods, q.SubmittedAt FROM (
                    SELECT s.UserId, s.Id, o.acc, s.Mods, s.SubmittedAt,
                           ROW_NUMBER() OVER (PARTITION BY s.UserId ORDER BY o.acc DESC, s.SubmittedAt) AS n
                    FROM {T}.scores s JOIN {S}.scores o ON o.id = s.Id
                    WHERE s.BeatmapId = {Db.N(newId)} AND s.Passed = 1 AND s.Mode = 3 AND o.acc >= {minAccuracy.ToString(CultureInfo.InvariantCulture)}
                      AND (s.Mods & ~{Db.N(AllowedMods)}) = 0
                ) q WHERE q.n = 1
                """).ConfigureAwait(false);
        }
        return $"{Db.N(courses)} dan courses, {Db.N(clears)} clears, {Db.N(skipped)} dans skipped";
    }

    /// <summary>osu-web's <c>approved</c> for a bancho.py status: ranked 1, approved 2, qualified 3, loved 4, else 0.</summary>
    private static int Approved(int status) => status switch { 2 => 1, 3 => 2, 4 => 3, 5 => 4, _ => 0 };

    public Task<int> EnsureTableAsync() =>
        _db.ExecAsync($"CREATE TABLE IF NOT EXISTS {T}.bpy_import_custom_sets (old_set_id INT NOT NULL PRIMARY KEY, new_set_id INT NOT NULL, uploaded_at DATETIME(6) NOT NULL)");

    private sealed record OldDiff(int SetId, int Id, string Md5, string Version, int Status, bool Frozen, int? Owner, int? Uploader);
}
