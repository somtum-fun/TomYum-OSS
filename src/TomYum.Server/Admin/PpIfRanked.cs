using Microsoft.EntityFrameworkCore;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Performance;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;

namespace TomYum.Server.Admin;

/// <summary>
/// What a pass on a map without pp (loved, qualified, pending, graveyard) would be worth if the map were ranked:
/// its own judgements, combo and mods (a DT's speed, DA's stats), as its pp would be worked out on a ranked map.
/// Shown greyed out, never counted.
/// </summary>
public static class PpIfRanked
{
    public static void MapPpIfRanked(this RouteGroupBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        // ?website={score id} (a website play) or ?mode={id} (a Relax/Autopilot play's TomYum id): { pp } (null: none).
        admin.MapGet("/pp-if-ranked", GetAsync);
    }

    internal static async Task<IResult> GetAsync(ulong? website, long? mode, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (services.GetService<IPerformanceCalculator>() is not { } calculator)
            return Results.Ok(new { pp = (double?)null });

        PerformanceRequest? request = null;
        if (website is { } websiteId && services.GetService<OsuWebCommunity>() is { } community
            && await community.GetScoreCardAsync(websiteId, cancellationToken).ConfigureAwait(false) is { } card)
        {
            request = For(card);
        }
        else if (mode is { } modeId && await services.GetRequiredService<TomYumDbContext>().Scores.AsNoTracking()
                     .FirstOrDefaultAsync(s => s.Id == modeId, cancellationToken).ConfigureAwait(false) is { } score)
        {
            request = For(score);
        }
        return Results.Ok(new { pp = request is null ? null : await CalculateAsync(calculator, request, cancellationToken).ConfigureAwait(false) });
    }

    /// <summary>The pp of <paramref name="request"/>; null when it can't be worked out (the .osu unreachable, ...).</summary>
    public static async Task<double?> CalculateAsync(IPerformanceCalculator calculator, PerformanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calculator);
        try
        {
            var pp = await calculator.CalculateAsync(request, cancellationToken).ConfigureAwait(false);
            return pp > 0 ? Math.Round(pp, 2) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>A website play (stable or lazer) as the pp calculator takes it; null for a fail.</summary>
    public static PerformanceRequest? For(ScoreCard score)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (!score.Passed)
            return null;
        var mods = Mods.None;
        foreach (var acronym in score.Mods)
        {
            if (NowPlaying.ParseAcronyms(acronym) is { } known)
                mods |= known;
        }
        return new PerformanceRequest(
            score.BeatmapId, score.Ruleset, (int)mods, score.MaxCombo, score.Great, score.Ok, score.Meh, score.Miss, 0, 0, Passed: true,
            Lazer: !score.IsStable, SpeedChange: score.Rate, LazerScore: score.IsStable ? null : LazerScoreJson.FromScoreData(score.Data));
    }

    /// <summary>A Relax/Autopilot play (TomYum's) as the pp calculator takes it; null for a fail.</summary>
    public static PerformanceRequest? For(Score score)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (!score.Passed)
            return null;
        return new PerformanceRequest(
            score.BeatmapId, score.Mode % Domain.Enums.GameMode.RelaxOffset, score.Mods, score.MaxCombo,
            score.Count300, score.Count100, score.Count50, score.CountMiss, score.CountGeki, score.CountKatu, Passed: true,
            Lazer: score.FromLazer, LargeTickHits: score.LargeTickHits, SliderEndHits: score.SliderEndHits,
            SpeedChange: score.SpeedChange, LazerScore: score.LazerScore);
    }
}
