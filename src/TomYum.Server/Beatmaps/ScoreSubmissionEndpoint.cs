using System.Globalization;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;
using TomYum.Bancho;
using TomYum.Caching;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Performance;
using TomYum.Server.Achievements;
using TomYum.Server.Bancho;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Beatmaps;

public static class ScoreSubmissionEndpoint
{
    public static void MapScoreSubmission(this IEndpointRouteBuilder app, params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/web/osu-submit-modular-selector.php", HandleAsync)
            .RequireHost(hosts)
            .DisableAntiforgery();
        // What old clients (2013–2015, docs/old-clients.md) submit to: the same score, an older key and reply.
        app.MapPost(LegacyPath, HandleAsync)
            .RequireHost(hosts)
            .DisableAntiforgery();
    }

    /// <summary>The submission endpoint of old clients (before the selector, which came after 2015).</summary>
    internal const string LegacyPath = "/web/osu-submit-modular.php";

    /// <summary>The key old clients encrypt their score with when they send no <c>osuver</c>.</summary>
    internal const string LegacyKey = "h89f2-890h2h89b34g-h80g134n90133";

    private const int MinReplaySize = 24;

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        PlayerRegistry registry,
        IUserRepository users,
        IPasswordHasher hasher,
        BeatmapService beatmaps,
        IBeatmapRepository beatmapRepo,
        IScoreRepository scores,
        IUserStatsRepository userStats,
        IUserAchievementRepository achievements,
        IPerformanceCalculator pp,
        IScoreQueue scoreQueue,
        IOsuWebSync osuWebSync,
        TomYum.Plugins.Abstractions.IEventBus events,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("TomYum.ScoreSubmission");

        // osu!stable sends `multipart/form-data;+boundary=` — the `+` is non-standard;
        // RFC 2046 expects a space (or nothing) after `;`, so ASP.NET Core fails to extract
        // the boundary and returns an empty form. Normalise before the form is read.
        if (context.Request.ContentType?.Contains(";+boundary=", StringComparison.OrdinalIgnoreCase) == true)
            context.Request.ContentType = context.Request.ContentType.Replace(";+boundary=", "; boundary=", StringComparison.OrdinalIgnoreCase);

        var form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false)
            : FormCollection.Empty;

        // Old clients post to osu-submit-modular.php: no osuver (an older key) and no bmk (the map's
        // checksum is the score's first field), and they expect the older reply below.
        var legacy = string.Equals(context.Request.Path.Value, LegacyPath, StringComparison.OrdinalIgnoreCase);
        var osuver = form["osuver"].ToString();
        var bmk = form["bmk"].ToString();
        var ivBase64 = form["iv"].ToString();
        var scoreParams = form["score"];

        // Numbered builds (b1122, docs/old-clients.md) send the score unencrypted in the query
        // (?score=<the same fields>&pass=<md5>), and the replay as a file part named "replay".
        var plainScore = legacy && string.IsNullOrEmpty(ivBase64) && context.Request.Query["score"].ToString() is { Length: > 0 } inQuery
            ? inQuery
            : null;

        if (plainScore is null
            && ((!legacy && (string.IsNullOrEmpty(osuver) || string.IsNullOrEmpty(bmk))) || string.IsNullOrEmpty(ivBase64) || scoreParams.Count == 0))
        {
            logger.LogWarning("Score submission rejected: missing form fields (osuver/bmk/iv/score)");
            return Results.Text("error: no", "text/plain");
        }

        // The client sends the `score` field twice: [0] = encrypted score string, [1] = .osr file.
        // Read replay bytes now while the multipart body is still buffered.
        byte[]? replayBytes = null;
        var replayFiles = form.Files.GetFiles("score") is { Count: > 0 } scoreFiles ? scoreFiles : form.Files.GetFiles("replay");
        var replayFile = replayFiles.Count > 0 ? replayFiles[replayFiles.Count - 1] : null;
        if (replayFile is not null && replayFile.Length >= MinReplaySize)
        {
            using var ms = new MemoryStream((int)replayFile.Length);
            await replayFile.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            replayBytes = ms.ToArray();
        }

        // Decrypt first — the username is inside the payload at parts[1].
        // Format: beatmapMd5:username:clientMd5:n300:n100:n50:nGeki:nKatu:nMiss:score:maxCombo:perfect:grade:mods:passed:mode:date:version
        string decrypted;
        if (plainScore is not null)
        {
            decrypted = plainScore;
        }
        else
        {
            try
            {
                var keyRaw = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(osuver) ? LegacyKey : $"osu!-scoreburgr---------{osuver}");
                var keyBytes = new byte[32];
                Buffer.BlockCopy(keyRaw, 0, keyBytes, 0, Math.Min(keyRaw.Length, 32));
                var ivBytes = Convert.FromBase64String(ivBase64);
                var cipherText = Convert.FromBase64String(scoreParams[0]!);

                var engine = new RijndaelEngine(256);
                var cipher = new PaddedBufferedBlockCipher(new CbcBlockCipher(engine), new Pkcs7Padding());
                cipher.Init(false, new ParametersWithIV(new KeyParameter(keyBytes), ivBytes));
                var plain = new byte[cipher.GetOutputSize(cipherText.Length)];
                var len = cipher.ProcessBytes(cipherText, 0, cipherText.Length, plain, 0);
                len += cipher.DoFinal(plain, len);
                decrypted = Encoding.UTF8.GetString(plain, 0, len);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Score submission rejected: AES decryption failed (osuver='{Osuver}')", osuver);
                return Results.Text("error: no", "text/plain");
            }
        }

        var parts = decrypted.Split(':');
        if (parts.Length < 16)
        {
            logger.LogWarning("Score submission rejected: too few fields ({Count}) in decrypted payload", parts.Length);
            return Results.Text("error: no", "text/plain");
        }
        if (string.IsNullOrEmpty(bmk))
            bmk = parts[0];

        // Authenticate using username from payload + pass form field.
        // The Token header on /web/ is a client-generated hash, not the cho-token.
        var usernameFromScore = parts[1].Trim();
        var user = await users.GetBySafeNameAsync(User.MakeSafeName(usernameFromScore), cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            logger.LogWarning("Score submission rejected: user '{Username}' not found", usernameFromScore);
            return Results.Text("error: no", "text/plain");
        }

        var passwordMd5 = plainScore is null ? form["pass"].ToString() : context.Request.Query["pass"].ToString();
        // An unencrypted score always needs the password (nothing else ties it to the player).
        if ((plainScore is not null && string.IsNullOrEmpty(passwordMd5))
            || (!string.IsNullOrEmpty(passwordMd5) && !hasher.Verify(passwordMd5, user.PasswordBcrypt)))
        {
            logger.LogWarning("Score submission rejected: pass verification failed for '{Username}'", usernameFromScore);
            return Results.Text("error: no", "text/plain");
        }

        if (user.IsBot)
        {
            logger.LogWarning("Score submission rejected: '{Username}' is a bot account", usernameFromScore);
            return Results.Text("error: no", "text/plain");
        }

        var player = registry.GetByUserId(user.Id);

        if (!int.TryParse(parts[3], out var n300) ||
            !int.TryParse(parts[4], out var n100) ||
            !int.TryParse(parts[5], out var n50) ||
            !int.TryParse(parts[6], out var ngeki) ||
            !int.TryParse(parts[7], out var nkatu) ||
            !int.TryParse(parts[8], out var nmiss) ||
            !long.TryParse(parts[9], out var scoreValue) ||
            !int.TryParse(parts[10], out var maxCombo) ||
            !int.TryParse(parts[13], out var mods) ||
            !int.TryParse(parts[15], out var mode))
        {
            logger.LogWarning("Score submission rejected: failed to parse numeric fields from '{Decrypted}'", decrypted);
            return Results.Text("error: no", "text/plain");
        }

        var perfect = parts[11].Equals("True", StringComparison.OrdinalIgnoreCase);
        var passed = parts[14].Equals("True", StringComparison.OrdinalIgnoreCase);
        var grade = parts[12];

        // Old clients (docs/old-clients.md): their mods in today's numbering, and Co-op refused.
        var build = LegacyScoreRules.BuildOf(parts);
        mods = LegacyScoreRules.Mods(mods, build);
        if (LegacyScoreRules.IsRefused(mods, build))
        {
            logger.LogWarning("Score submission rejected: Co-op from an old client ({Build})", LegacyScoreRules.Describe(build));
            return Results.Text("error: no", "text/plain");
        }

        // Fold Relax/Autopilot into the combined gamemode used for stats + leaderboards so those
        // scores stay off the vanilla boards. The vanilla ruleset (`mode`) still drives pp and
        // accuracy. See TomYum.Domain.Enums.GameMode.
        var gameMode = ((Mods)mods).ToGameMode(mode);

        // ScoreV2 changes the scoring algorithm server-side — not supported yet
        if ((mods & (1 << 29)) != 0)
        {
            logger.LogWarning("Score submission rejected: ScoreV2 is not supported");
            return Results.Text("error: no", "text/plain");
        }

        var beatmap = await beatmaps.GetByMd5Async(bmk, cancellationToken).ConfigureAwait(false);
        if (beatmap is null)
        {
            logger.LogWarning("Score submission rejected: beatmap MD5 '{Bmk}' not found", bmk);
            return Results.Text("error: no", "text/plain");
        }

        // Persist beatmap metadata so tools like RecalcPp can join against the beatmaps table.
        // Best-effort: a DB hiccup must not block the submission response.
        try
        {
            await beatmapRepo.UpsertAsync(beatmap, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Beatmap upsert failed for {BeatmapId}; submission continues", beatmap.Id);
        }

        // First play of a set this run: store its other difficulties too, so the website shows the
        // whole set (with CS/AR/OD/HP etc.). Best-effort like the upsert above.
        try
        {
            foreach (var difficulty in await beatmaps.FetchSetOnceAsync(beatmap.SetId, cancellationToken).ConfigureAwait(false))
            {
                if (difficulty.Id != beatmap.Id)
                    await beatmapRepo.UpsertAsync(difficulty, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Importing beatmap set {SetId} failed; submission continues", beatmap.SetId);
        }

        // Fetch before insert so the Status==Best filter returns the genuine prior best, not this score
        var prevBest = await scores.GetPersonalBestAsync(user.Id, bmk, gameMode, cancellationToken).ConfigureAwait(false);

        // Fetch the leaderboard before insert to determine the player's beatmap rank before this submission.
        // After insert the position of the previous best is no longer queryable.
        var preBoardForRank = await scores.GetLeaderboardAsync(bmk, gameMode, 50, user.Id, cancellationToken).ConfigureAwait(false);
        var prevBeatmapRank = prevBest is not null
            ? preBoardForRank.TakeWhile(s => s.Id != prevBest.Id).Count() + 1 as int?
            : null;
        // If prevBest wasn't in the top-50 window it won't be found; treat as no previous rank.
        if (prevBeatmapRank > preBoardForRank.Count) prevBeatmapRank = null;

        var ppValue = 0.0;
        if (beatmap.Status.AwardsRankedPp())
        {
            try
            {
                ppValue = await pp.CalculateAsync(
                    new PerformanceRequest(
                        BeatmapId: beatmap.Id,
                        RulesetId: mode,
                        Mods: mods,
                        Count300: n300,
                        Count100: n100,
                        Count50: n50,
                        CountMiss: nmiss,
                        CountGeki: ngeki,
                        CountKatu: nkatu,
                        MaxCombo: maxCombo,
                        Passed: passed),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // pp calculation is best-effort — never fail submission over it — but surface the
                // reason so a misconfigured backend / calc failure isn't silently recorded as 0 pp.
                logger.LogWarning(ex, "PP calc failed for beatmap {BeatmapId} gamemode {GameMode}; recording 0 pp", beatmap.Id, gameMode);
            }
        }

        // A pp rework in this mode: the play's pp waits (not a best, not in the totals) until it's
        // done, when it's worked out with everything else.
        var held = passed && TomYum.Persistence.RankingPause.IsPaused(gameMode);

        // An old client's passed play in taiko or catch whose judgements don't cover the map as today's
        // client converts it (docs/old-clients.md): kept, but off the leaderboard and out of the totals.
        var offBoard = false;
        if (passed && build.IsLegacy && mode is 1 or 2 && pp is IBeatmapDifficulty converted)
        {
            try
            {
                var expected = (await converted.DifficultyAsync(beatmap.Id, mode, cancellationToken).ConfigureAwait(false))?.MaxCombo;
                if (!LegacyScoreRules.JudgementsMatch(mode, n300, n100, nmiss, expected))
                {
                    offBoard = true;
                    ppValue = 0;
                    logger.LogWarning("Score from an old client ({Build}) kept off the leaderboard: {Hits} judgements on beatmap {BeatmapId} mode {Mode}, today's client has {Expected}.",
                        LegacyScoreRules.Describe(build), n300 + n100 + nmiss, beatmap.Id, mode, expected);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Couldn't check an old client's judgements on beatmap {BeatmapId}; the score counts", beatmap.Id);
            }
        }

        // A passed score is Best only if the player has no prior best or this score beats it by pp.
        // Old best scores remain in the DB as Submitted (not shown on leaderboard) for history.
        var scoreStatus = passed
            ? (!held && !offBoard && (prevBest is null || ppValue > prevBest.Pp) ? ScoreStatus.Best : ScoreStatus.Submitted)
            : ScoreStatus.Failed;

        // Plugins may reject the score before anything is stored (e.g. a mode's own rules).
        var submitting = new TomYum.Plugins.Abstractions.ScoreSubmittingEvent(
            user.Id, bmk, (byte)gameMode, (uint)mods, ComputeAccuracy(mode, n300, n100, n50, ngeki, nkatu, nmiss),
            maxCombo, passed, beatmap.FullName);
        await events.PublishAsync(submitting, cancellationToken).ConfigureAwait(false);
        if (submitting.IsVetoed)
        {
            logger.LogInformation("A plugin rejected {User}'s score on {Beatmap}.", user.Username, beatmap.Id);
            return Results.Text("error: no", "text/plain");
        }

        if (scoreStatus == ScoreStatus.Best && prevBest is not null)
            await scores.MarkPreviousBestAsync(user.Id, bmk, gameMode, cancellationToken).ConfigureAwait(false);

        var score = await scores.AddAsync(new Score
        {
            UserId = user.Id,
            BeatmapId = beatmap.Id,
            BeatmapMd5 = bmk,
            Mode = gameMode,
            ScoreValue = scoreValue,
            MaxCombo = maxCombo,
            Count300 = n300,
            Count100 = n100,
            Count50 = n50,
            CountMiss = nmiss,
            CountGeki = ngeki,
            CountKatu = nkatu,
            Grade = grade,
            Mods = mods,
            Perfect = perfect,
            Passed = passed,
            Pp = ppValue,
            Status = scoreStatus,
            SubmittedAt = DateTimeOffset.UtcNow,
            PpHeld = held,
        }, cancellationToken).ConfigureAwait(false);

        // Persist replay file: data/replays/{scoreId}.osr (configurable via Replays:Path).
        // Only for passed scores with a valid replay; silently skipped otherwise.
        if (passed && replayBytes is not null)
        {
            var replayDir = configuration["Replays:Path"] ?? "data/replays";
            Directory.CreateDirectory(replayDir);
            var replayPath = Path.Combine(replayDir, $"{score.Id}.osr");
            await File.WriteAllBytesAsync(replayPath, replayBytes, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Replay saved: {Path} ({Bytes} bytes)", replayPath, replayBytes.Length);
        }

        // Enqueue for async post-processing (spectator broadcast, achievements, etc.).
        // Best-effort: a Redis hiccup must not fail the submission response.
        try
        {
            await scoreQueue.EnqueueAsync(score.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Score {ScoreId} could not be enqueued; submission still succeeds", score.Id);
        }

        var stats = await userStats.GetOrCreateAsync(user.Id, gameMode, cancellationToken).ConfigureAwait(false);

        var prevRankedScore = stats.RankedScore;
        var prevTotalScore  = stats.TotalScore;
        var prevPp          = stats.Pp;
        var prevAccuracy    = stats.Accuracy;

        stats.PlayCount++;
        stats.TotalScore += scoreValue;

        if (scoreStatus == ScoreStatus.Best)
        {
            // Ranked score is the delta: new score minus the previous best score on this map.
            // If the player had no prior best, the full score value is added.
            // Clamp to 0 so ranked score can never go negative (e.g. a retry that somehow
            // ends up as Best with a lower score value due to PP > prevBest.PP).
            var delta = scoreValue - (prevBest?.ScoreValue ?? 0);
            stats.RankedScore = Math.Max(0, stats.RankedScore + delta);

            // Weighted PP and accuracy across all best scores (standard osu! formula).
            var bestPerfs = await scores.GetBestPerformancesAsync(user.Id, gameMode, cancellationToken).ConfigureAwait(false);
            if (PerformanceTotals.Compute(bestPerfs) is { } totals)
            {
                stats.Pp = totals.Pp;
                stats.Accuracy = totals.Accuracy;
            }
        }

        var prevGlobalRank = await userStats.GetGlobalRankAsync(user.Id, gameMode, cancellationToken).ConfigureAwait(false);

        stats = await userStats.UpdateAsync(stats, cancellationToken).ConfigureAwait(false);
        var globalRank = await userStats.GetGlobalRankAsync(user.Id, gameMode, cancellationToken).ConfigureAwait(false);

        // Website (osu-web) copy of the score + stats. Best-effort: the game must not fail over it.
        try
        {
            // With ppy's score processor, pp and accuracy combine stable and lazer scores: take those.
            if (await osuWebSync.ScoreSubmittedAsync(score, cancellationToken).ConfigureAwait(false) is { } combined)
            {
                stats.Pp = combined.Pp;
                stats.Accuracy = combined.Accuracy;
                globalRank = await userStats.GetGlobalRankAsync(user.Id, gameMode, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "osu-web sync failed for score {ScoreId}; submission continues", score.Id);
        }

        if (player is not null)
        {
            var presence = player.Presence;
            if (gameMode == presence.GameMode)
            {
                var filler = context.RequestServices.GetService<PresenceStats>() ?? new PresenceStats(userStats);
                await filler.FillAsync(presence, user.Id, gameMode, stats, cancellationToken).ConfigureAwait(false);

                var writer = new BanchoPacketWriter(128);
                LoginResponseBuilder.WriteStats(writer, presence);
                registry.EnqueueAbout(player, writer.ToArray());
            }
        }

        var leaderboard = await scores.GetLeaderboardAsync(bmk, gameMode, 50, user.Id, cancellationToken).ConfigureAwait(false);
        var scoreRank = leaderboard.TakeWhile(s => s.Id != score.Id).Count() + 1;

        var totalHits = n300 + n100 + n50 + nmiss;
        var accuracy = ComputeAccuracy(mode, n300, n100, n50, ngeki, nkatu, nmiss);

        var accuracyPct = accuracy * 100.0;
        var prevBestScore = prevBest is not null ? prevBest.ScoreValue : (long?)null;
        var prevBestCombo = prevBest is not null ? prevBest.MaxCombo : (int?)null;
        var prevBestAccPct = prevBest is not null
            ? ComputeAccuracy(mode, prevBest.Count300, prevBest.Count100, prevBest.Count50,
                              prevBest.CountGeki, prevBest.CountKatu, prevBest.CountMiss) * 100.0
            : (double?)null;
        var prevBestPp = prevBest is not null ? prevBest.Pp : (double?)null;

        // Achievements (medals): unlock any newly-earned medal and hand them to the client via the
        // overall chart's `achievements-new:` field, which drives the in-game unlock popups. osu!'s gate:
        // a passed play on a map with a leaderboard, by a player who isn't restricted (MedalService).
        // Best-effort — a medal/DB hiccup must never fail the submission response.
        IReadOnlyList<Medal> newMedals = [];
        if (MedalService.Eligible(passed, beatmap.Status, (Mods)mods, user.IsRestricted))
        {
            try
            {
                var stars = await MedalService.StarRatingAsync(pp, beatmap, mode, mods, cancellationToken).ConfigureAwait(false);
                var check = new MedalCheck(mode, (Mods)mods, maxCombo, stars, perfect, beatmap.Status, beatmap.Id);
                newMedals = await MedalService.AwardAsync(achievements, user.Id, check, DateTime.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Achievement check failed for user {UserId} on beatmap {BeatmapId}; submission continues", user.Id, beatmap.Id);
            }
        }
        if (newMedals.Count > 0)
        {
            try
            {
                await osuWebSync.MedalsUnlockedAsync(user.Id, newMedals, beatmap.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "osu-web medal sync failed for user {UserId}; submission continues", user.Id);
            }
        }
        var achievementsNew = MedalService.FormatAchievementsNew(newMedals);

        if (legacy)
        {
            // The older reply: the map line and the overall chart only (the player's beatmap rank in it),
            // accuracy as a fraction, medals by their icon's file name (osuTitanic's deck answers alike).
            // Old clients parse these values: no empty fields (a first play has no rank before: 0), and
            // approvedDate is a date (the map's last update stands in for its ranked date).
            var prevPlayCount = stats.PlayCount - 1;
            var approvedDate = beatmap.LastUpdate.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var legacyResponse = string.Create(CultureInfo.InvariantCulture,
                $"beatmapId:{beatmap.Id}|beatmapSetId:{beatmap.SetId}|beatmapPlaycount:1|beatmapPasscount:1|approvedDate:{approvedDate}\n" +
                $"chartId:overall|chartName:Overall Ranking|chartUrl:https://osu.ppy.sh/u/{user.Id}|chartEndDate:|achievements:{string.Join(' ', newMedals.Select(m => m.File))}|achievements-new:|" +
                $"rankBefore:{prevGlobalRank}|rankAfter:{globalRank}|rankedScoreBefore:{prevRankedScore}|rankedScoreAfter:{stats.RankedScore}|totalScoreBefore:{prevTotalScore}|totalScoreAfter:{stats.TotalScore}|" +
                $"playCountBefore:{prevPlayCount}|playCountAfter:{stats.PlayCount}|ppBefore:{prevPp:F0}|ppAfter:{stats.Pp:F0}|accuracyBefore:{prevAccuracy:0.####}|accuracyAfter:{stats.Accuracy:0.####}|" +
                $"onlineScoreId:{score.Id}|toNextRankUser:|toNextRank:0|beatmapRankingBefore:{prevBeatmapRank ?? 0}|beatmapRankingAfter:{scoreRank}");
            logger.LogInformation("Old-client score reply for {User} ({Version}): {Reply}", user.Username, player?.Build.Version, legacyResponse);
            return Results.Text(legacyResponse, "text/plain");
        }

        var response = string.Create(CultureInfo.InvariantCulture,
            $"beatmapId:{beatmap.Id}|beatmapSetId:{beatmap.SetId}|beatmapPlaycount:1|beatmapPasscount:1|approvedDate:\n\n" +
            $"chartId:beatmap|chartUrl:https://osu.ppy.sh/b/{beatmap.Id}|chartName:Beatmap Ranking|rankBefore:{prevBeatmapRank}|rankAfter:{scoreRank}|maxComboBefore:{prevBestCombo}|maxComboAfter:{maxCombo}|accuracyBefore:{prevBestAccPct:F2}|accuracyAfter:{accuracyPct:F2}|rankedScoreBefore:{prevBestScore}|rankedScoreAfter:{scoreValue}|totalScoreBefore:{prevBestScore}|totalScoreAfter:{scoreValue}|ppBefore:{prevBestPp:F2}|ppAfter:{ppValue:F2}|onlineScoreId:{score.Id}\n\n" +
            $"chartId:overall|chartUrl:https://osu.ppy.sh/u/{user.Id}|chartName:Overall Ranking|rankBefore:{prevGlobalRank}|rankAfter:{globalRank}|accuracyBefore:{prevAccuracy * 100.0:F2}|accuracyAfter:{stats.Accuracy * 100.0:F2}|rankedScoreBefore:{prevRankedScore}|rankedScoreAfter:{stats.RankedScore}|totalScoreBefore:{prevTotalScore}|totalScoreAfter:{stats.TotalScore}|ppBefore:{prevPp:F0}|ppAfter:{stats.Pp:F0}|toNextRank:|toNextRankUser:|achievements-new:{achievementsNew}\n");

        return Results.Text(response, "text/plain");
    }

    private static double ComputeAccuracy(int mode, int n300, int n100, int n50, int ngeki, int nkatu, int nmiss) =>
        AccuracyCalculator.Compute(mode, n300, n100, n50, ngeki, nkatu, nmiss);
}
