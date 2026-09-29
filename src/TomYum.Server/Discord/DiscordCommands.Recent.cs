using System.Globalization;
using System.Text;
using TomYum.Performance;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;

namespace TomYum.Server.Discord;

/// <summary>
/// <c>/recent</c>, Bathbot-style: "Try #N" (plays in a row on that map), the player (avatar) with pp and
/// ranks, which client it was played on, the map with stars (with mods), the grade and completion, pp out of the map's max and the
/// full-combo pp, combo and misses, the map's numbers with mods, and buttons to page through the
/// player's recent plays.
/// </summary>
public sealed partial class DiscordCommands
{
    /// <summary>How far back the recent plays go (the buttons page through these).</summary>
    public const int RecentDepth = 50;

    /// <summary>The <c>/recent</c> card for the player's <paramref name="index"/>-th latest play (0 = latest); null when there's none.</summary>
    public async Task<DiscordPost?> RecentCardAsync(IServiceProvider services, OsuWebCommunity community, PlayerCard player, int index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(community);
        ArgumentNullException.ThrowIfNull(player);
        var plays = await community.GetRecentAsync(player.UserId, player.Ruleset, RecentDepth, cancellationToken).ConfigureAwait(false);
        if (plays.Count == 0)
            return null;
        index = Math.Clamp(index, 0, plays.Count - 1);
        var score = plays[index];

        // "Try #N": this play and the ones just before it on the same map.
        var tries = 1;
        while (index + tries < plays.Count && plays[index + tries].BeatmapId == score.BeatmapId)
            tries++;

        var map = await community.GetMapAsync(score.BeatmapId, cancellationToken).ConfigureAwait(false);
        var mods = LegacyMods(score.Mods);
        IReadOnlyList<double>? pp = null;
        var playPp = score.Pp;
        var performance = services.GetService<IPerformanceCalculator>();
        if (performance is IAccuracyPerformance calculator)
        {
            try
            {
                // The full-combo pp at this accuracy, and the map's max (SS).
                pp = await calculator.ForAccuraciesAsync(score.BeatmapId, score.Ruleset, (int)mods, [score.Accuracy, 1.0], cancellationToken).ConfigureAwait(false);
                // A fail has no pp of its own: what the part it got through is worth.
                if (!score.Passed && performance is IPartialPerformance partial)
                {
                    playPp = await partial.PartialAsync(new PerformanceRequest(
                        score.BeatmapId, score.Ruleset, (int)mods, score.MaxCombo, score.Great, score.Ok, score.Meh, score.Miss, 0, 0, Passed: false),
                        cancellationToken).ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031 // pp is a bonus: the .osu may be unreachable, the card still helps
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        var embed = new DiscordEmbed
        {
            AuthorName = $"{player.Username}: {Pp2(player.Pp)}pp ({Ranks(player)})",
            AuthorUrl = _format.ProfileIn(player.UserId, player.Ruleset),
            AuthorIconUrl = _format.Avatar(player.UserId),
            Title = $"{score.Artist} - {score.Title} [{score.Version}] [{score.StarRating.ToString("0.00", CultureInfo.InvariantCulture)}★]",
            Url = _format.Beatmap(score.BeatmapId),
            ThumbnailUrl = _format.ListCover(score.BeatmapsetId),
            Description = RecentLines(score, map, mods, pp, playPp),
            Color = score.Passed ? DiscordFormat.Accent : DiscordFormat.Danger,
            Footer = (score.IsStable ? "Played on osu!stable" : "Played on osu!lazer") + (map is null ? string.Empty : " • " + MapFooter(map)),
        };
        return new DiscordPost(string.Create(CultureInfo.InvariantCulture, $"Try #{tries}"), embed)
        {
            Buttons = RecentButtons(player.UserId, player.Ruleset, index, plays.Count),
        };
    }

    /// <summary>The paging buttons: first, previous, refresh, next, last (older plays are further right).</summary>
    public static IReadOnlyList<DiscordButton> RecentButtons(int userId, int ruleset, int index, int count)
    {
        string Id(int to, string slot) => string.Create(CultureInfo.InvariantCulture, $"rs:{userId}:{ruleset}:{to}:{slot}");
        var last = Math.Max(0, count - 1);
        return
        [
            new DiscordButton("⏮", DiscordButtonStyle.Secondary, Id(0, "a")) { Disabled = index == 0 },
            new DiscordButton("◀", DiscordButtonStyle.Secondary, Id(Math.Max(0, index - 1), "b")) { Disabled = index == 0 },
            new DiscordButton("🔄", DiscordButtonStyle.Primary, Id(index, "c")),
            new DiscordButton("▶", DiscordButtonStyle.Secondary, Id(Math.Min(last, index + 1), "d")) { Disabled = index >= last },
            new DiscordButton("⏭", DiscordButtonStyle.Secondary, Id(last, "e")) { Disabled = index >= last },
        ];
    }

    /// <summary>"#1 NL1": global rank and country rank.</summary>
    private static string Ranks(PlayerCard player)
    {
        var global = player.GlobalRank is { } rank ? "#" + DiscordFormat.Number(rank) : "unranked";
        var country = player.CountryRank is { } local ? player.Country.ToUpperInvariant() + DiscordFormat.Number(local) : player.Country.ToUpperInvariant();
        return $"{global} {country}";
    }

    private static string Pp2(double pp) => pp.ToString("#,0.00", CultureInfo.InvariantCulture);

    /// <summary>The lazer acronyms the pp calculator knows, as the stable mod bitmask.</summary>
    private static TomYum.Bancho.Mods LegacyMods(IReadOnlyList<string> acronyms)
    {
        var mods = TomYum.Bancho.Mods.None;
        foreach (var acronym in acronyms)
        {
            if (NowPlaying.ParseAcronyms(acronym) is { } known)
                mods |= known;
        }
        return mods;
    }

    /// <summary>The three lines under the title (see the class remarks).</summary>
    private static string RecentLines(ScoreCard score, MapCard? map, TomYum.Bancho.Mods mods, IReadOnlyList<double>? pp, double? playPp)
    {
        var total = score.IsStable && score.LegacyTotalScore > 0 ? score.LegacyTotalScore : score.TotalScore;
        var completion = !score.Passed && map is { ObjectCount: > 0 }
            ? string.Create(CultureInfo.InvariantCulture, $"@{Math.Min(100, 100.0 * (score.Great + score.Ok + score.Meh + score.Miss) / map.ObjectCount):0}% ")
            : string.Empty;
        var modText = score.Mods.Count > 0 ? "+" + string.Concat(score.Mods) : "+NM";
        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"{DiscordFormat.Grade(score.Grade)} {completion}{modText}  {DiscordFormat.Number(total)}  {DiscordFormat.Percent(score.Accuracy)}  {DiscordFormat.Relative(score.EndedAt)}\n");

        var fullCombo = score.Miss == 0 && (score.BeatmapMaxCombo == 0 || score.MaxCombo >= score.BeatmapMaxCombo - 1);
        var ppText = playPp is { } value ? value.ToString("0.00", CultureInfo.InvariantCulture) : "-";
        text.Append(CultureInfo.InvariantCulture, $"**{ppText}**");
        if (pp is [var ifFc, var max])
        {
            text.Append(CultureInfo.InvariantCulture, $"/{max:0.00}pp");
            if (!fullCombo)
                text.Append(CultureInfo.InvariantCulture, $" ~~({ifFc:0.00}pp)~~");
        }
        else
        {
            text.Append("pp");
        }
        var combo = score.BeatmapMaxCombo > 0
            ? string.Create(CultureInfo.InvariantCulture, $"**{score.MaxCombo}x**/{score.BeatmapMaxCombo}x")
            : string.Create(CultureInfo.InvariantCulture, $"**{score.MaxCombo}x**");
        var misses = fullCombo && score.Passed ? "FC" : string.Create(CultureInfo.InvariantCulture, $"{score.Miss} ❌");
        text.Append(CultureInfo.InvariantCulture, $" • {combo} • {misses}");

        if (map is not null)
        {
            var (cs, ar, od, hp, rate) = WithMods(map, mods, score.Ruleset);
            var length = TimeSpan.FromSeconds(Math.Round(map.TotalLength / rate));
            text.Append(CultureInfo.InvariantCulture,
                $"\n`{(int)length.TotalMinutes:00}:{length.Seconds:00}` • `CS: {cs:0.#} AR: {ar:0.#} OD: {od:0.#} HP: {hp:0.#}` • 🎵 {map.Bpm * rate:0.#}");
        }
        return text.ToString();
    }

    /// <summary>"Mapset by X • Ranked 2025-06-28 11:31 UTC" (or when it was last updated, for maps without a leaderboard).</summary>
    private static string MapFooter(MapCard map)
    {
        var (status, at) = map.Status switch
        {
            1 => ("Ranked", map.RankedAt),
            2 => ("Approved", map.RankedAt),
            3 => ("Qualified", map.RankedAt),
            4 => ("Loved", map.RankedAt),
            _ => ("Last updated", map.UpdatedAt),
        };
        var when = at is { } date ? $" • {status} {date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC" : string.Empty;
        return $"Mapset by {map.Creator}{when}";
    }

    /// <summary>The map's CS, AR, OD and HP with HR/EZ and rate mods (as osu! works them out), and the rate.</summary>
    public static (double Cs, double Ar, double Od, double Hp, double Rate) WithMods(MapCard map, TomYum.Bancho.Mods mods, int ruleset)
    {
        ArgumentNullException.ThrowIfNull(map);
        double cs = map.CircleSize, ar = map.ApproachRate, od = map.OverallDifficulty, hp = map.DrainRate;
        if (mods.HasFlag(TomYum.Bancho.Mods.HardRock))
        {
            cs = Math.Min(10, cs * 1.3);
            ar = Math.Min(10, ar * 1.4);
            od = Math.Min(10, od * 1.4);
            hp = Math.Min(10, hp * 1.4);
        }
        else if (mods.HasFlag(TomYum.Bancho.Mods.Easy))
        {
            cs *= 0.5;
            ar *= 0.5;
            od *= 0.5;
            hp *= 0.5;
        }

        var rate = mods.HasFlag(TomYum.Bancho.Mods.DoubleTime) || mods.HasFlag(TomYum.Bancho.Mods.Nightcore) ? 1.5
            : mods.HasFlag(TomYum.Bancho.Mods.HalfTime) ? 0.75 : 1.0;
        if (rate != 1.0 && ruleset == 0)
        {
            // Approach time and the 300 window shrink with the rate; turn them back into AR and OD.
            var preempt = (ar < 5 ? 1800 - 120 * ar : 1950 - 150 * ar) / rate;
            ar = preempt > 1200 ? (1800 - preempt) / 120 : (1950 - preempt) / 150;
            var window = (80 - 6 * od) / rate;
            od = (80 - window) / 6;
        }
        return (cs, ar, od, hp, rate);
    }
}
