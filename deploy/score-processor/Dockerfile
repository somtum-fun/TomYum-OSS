# ppy's score processor (osu-queue-score-statistics: lazer pp, totals, medals) for TomYum, built from
# ppy's source with one change: besides osu!'s own ranked mods, mods staff mark as ranked in the
# website's admin panel (Admin panel > Mods) count too, for pp and medals, and old scores with a
# mod that changed get their pp worked out again (TomYumRankedMods.cs), and Relax/Autopilot plays
# wait for TomYum's import before they're processed (TomYumRelaxWait.cs). See docs/lazer.md.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
ARG PROCESSOR_REPO=https://github.com/ppy/osu-queue-score-statistics
ARG PROCESSOR_REF=7558b004f33cb79196b63682daa9862cb9c23faa
RUN git clone "$PROCESSOR_REPO" /src && git -C /src checkout "$PROCESSOR_REF"
WORKDIR /src/osu.Server.Queues.ScoreStatisticsProcessor
COPY TomYumRankedMods.cs Helpers/TomYumRankedMods.cs
# pp: every mod has to be ranked, osu!'s way or ours.
RUN grep -q 'return modsToCheck.All(m => m.Ranked);' Processors/ScorePerformanceProcessor.cs \
    && sed -i 's/return modsToCheck.All(m => m.Ranked);/return modsToCheck.All(global::osu.Server.Queues.ScoreStatisticsProcessor.Helpers.TomYumRankedMods.IsRanked);/' Processors/ScorePerformanceProcessor.cs
# Medals: unranked mods count as difficulty reduction there.
RUN grep -q 'if (!mod.Ranked)' Helpers/MedalHelpers.cs \
    && sed -i 's/if (!mod.Ranked)/if (!TomYumRankedMods.IsRanked(mod))/' Helpers/MedalHelpers.cs
# Start watching the panel's choices with the queue.
RUN grep -q '            queue.Run(cancellationToken);' Commands/Queue/WatchQueueCommand.cs \
    && sed -i 's/^            queue.Run(cancellationToken);/            global::osu.Server.Queues.ScoreStatisticsProcessor.Helpers.TomYumRankedMods.Start(cancellationToken);\n            queue.Run(cancellationToken);/' Commands/Queue/WatchQueueCommand.cs
# Relax/Autopilot plays: processed once TomYum has taken them in (TomYumRelaxWait.cs), so the game's
# results screen shows the play counted in the player's relax/autopilot statistics.
COPY TomYumRelaxWait.cs Helpers/TomYumRelaxWait.cs
RUN grep -q '                    var score = item.Score;' ScoreStatisticsQueueProcessor.cs \
    && sed -i 's/^                    var score = item.Score;/                    var score = item.Score;\n                    global::osu.Server.Queues.ScoreStatisticsProcessor.Helpers.TomYumRelaxWait.WaitForTomYum(conn, score);/' ScoreStatisticsQueueProcessor.cs
# Pre-2024 lazer builds send no total without mods: it's worked back out from the total (TomYumOldLazer.cs)
# instead of the score being refused.
COPY TomYumOldLazer.cs Helpers/TomYumOldLazer.cs
RUN grep -q 'throw new ProcessingAbortedException("Score is missing total score without mods.");' Processors/ScoreMultiplierValidator.cs \
    && sed -i 's/throw new ProcessingAbortedException("Score is missing total score without mods.");/totalScoreWithoutMods = global::osu.Server.Queues.ScoreStatisticsProcessor.Helpers.TomYumOldLazer.TotalScoreWithoutMods(score, conn, transaction);/' Processors/ScoreMultiplierValidator.cs
# Global ranks the way TomYum works them out: visible players only, and the rankings' inactivity rule
# (TomYumRankIndex.cs, TOMYUM_RANKING_INACTIVE_DAYS).
COPY TomYumRankIndex.cs Helpers/TomYumRankIndex.cs
RUN grep -q 'userStats.rank_score_index = await getUserRankScoreIndex(userStats, connection, transaction, dbInfo);' Processors/UserTotalPerformanceProcessor.cs \
    && sed -i 's/userStats.rank_score_index = await getUserRankScoreIndex(userStats, connection, transaction, dbInfo);/userStats.rank_score_index = await global::osu.Server.Queues.ScoreStatisticsProcessor.Helpers.TomYumRankIndex.GetAsync(userStats, dbInfo.UserStatsTable, connection, transaction);/' Processors/UserTotalPerformanceProcessor.cs
# ppy.osu.Game brings MessagePack 3.1.7: 3.1.9 fixes a denial of service (CVE-2026-92707), 3.1.10 a ReDoS.
RUN dotnet add package MessagePack --version 3.1.12
RUN dotnet publish -c Release -o /out && rm -rf /out/runtimes /out/osu.Game.Resources.dll

FROM mcr.microsoft.com/dotnet/runtime:10.0
# ca-certificates: the base image's are older than Ubuntu's fix for the eMudhra CA key compromise
# (CVE-2026-111943, 20260816ubuntu0.24.04.1): upgrade them until Microsoft's image has it.
RUN apt-get update && apt-get install -y --only-upgrade ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build-env /out .
ENTRYPOINT ["dotnet", "osu.Server.Queues.ScoreStatisticsProcessor.dll"]
