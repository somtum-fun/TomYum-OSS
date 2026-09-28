using System.Globalization;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Performance;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Admin;
using TomYum.Server.Beatmaps;

namespace TomYum.Server.Bancho;

/// <summary>
/// BanchoBot and <c>/np</c>: the reply to a <c>/np</c> sent to BanchoBot (the map, its status here
/// and pp for full combos at 95–100%), and <c>!map &lt;status&gt; [set|map]</c>, which sets the
/// status of the map the caller last <c>/np</c>'d exactly like the BN panel does (same log,
/// recalculation, website and Discord). <c>!map</c> is for the website's BN, NAT and admins.
/// </summary>
public sealed class MapCommands
{
    /// <summary>The <c>!map</c> words and the status each sets (<see cref="BeatmapStatusChanger.UnlockName"/> = bancho's own).</summary>
    public static readonly IReadOnlyDictionary<string, string> Statuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["rank"] = "ranked",
        ["ranked"] = "ranked",
        ["approve"] = "approved",
        ["approved"] = "approved",
        ["qualify"] = "qualified",
        ["qualified"] = "qualified",
        ["love"] = "loved",
        ["loved"] = "loved",
        ["unrank"] = "pending",
        ["pending"] = "pending",
        ["reset"] = BeatmapStatusChanger.UnlockName,
        ["bancho"] = BeatmapStatusChanger.UnlockName,
    };

    /// <summary>Website groups that may use <c>!map</c> (with administrators and developers).</summary>
    private static readonly HashSet<string> MapperGroups = new(StringComparer.Ordinal) { "bng", "bng_limited", "nat", "admin" };

    private static readonly double[] Accuracies = [0.95, 0.98, 0.99, 1.0];

    private readonly IServiceScopeFactory _scopes;
    private readonly BeatmapService _beatmaps;
    private readonly string _site;

    public MapCommands(IServiceScopeFactory scopes, BeatmapService beatmaps, IConfiguration configuration)
    {
        _scopes = scopes;
        _beatmaps = beatmaps;
        _site = $"https://osu.{BanchoHosts.ResolveDomain(configuration)}";
    }

    /// <summary>BanchoBot's reply to a <c>/np</c>: the map, its status and pp (osu! maps).</summary>
    public async Task<string> DescribeAsync(NowPlaying np, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(np);

        using var scope = _scopes.CreateScope();
        var beatmap = await FindAsync(scope.ServiceProvider, np, cancellationToken).ConfigureAwait(false);
        if (beatmap is null)
            return "I don't know that map. Play it once or ask staff to import it.";

        var status = _beatmaps.GetStatusOverride(beatmap.Id) ?? beatmap.Status;
        var mods = ModNames(np.Mods);
        var parts = new List<string>
        {
            $"[{_site}/b/{beatmap.Id.ToString(CultureInfo.InvariantCulture)} {beatmap.FullName}]{(mods.Length > 0 ? " +" + mods : string.Empty)}",
            "★" + beatmap.StarRating.ToString("0.00", CultureInfo.InvariantCulture),
            StatusName(status),
        };

        if (scope.ServiceProvider.GetService<IPerformanceCalculator>() is IAccuracyPerformance calculator)
        {
            try
            {
                var pp = await calculator.ForAccuraciesAsync(beatmap.Id, np.Mode, (int)np.Mods, Accuracies, cancellationToken).ConfigureAwait(false);
                if (pp is { Count: 4 })
                {
                    parts.Add(string.Join(" | ", Accuracies.Select((acc, i) =>
                        string.Create(CultureInfo.InvariantCulture, $"{acc * 100:0}%: {pp[i]:0.##}pp"))));
                }
            }
#pragma warning disable CA1031 // pp is a bonus: the .osu may be unreachable; the rest of the reply still helps
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        return string.Join(" | ", parts);
    }

    /// <summary><c>!map &lt;status&gt; [set|map]</c> on the caller's last <c>/np</c>.</summary>
    public async Task<string> RunAsync(OnlinePlayer caller, string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(args);

        const string usage = "Usage: /np a map, then !map <rank|approve|qualify|love|unrank|reset> [set|map] (set if left out).";
        if (args.Length == 0 || !Statuses.TryGetValue(args[0], out var status))
            return usage;
        var wholeSet = args.Length < 2 || args[1].Equals("set", StringComparison.OrdinalIgnoreCase);
        if (!wholeSet && !args[1].Equals("map", StringComparison.OrdinalIgnoreCase))
            return usage;

        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        if (!await MayChangeStatusAsync(services, caller.UserId, cancellationToken).ConfigureAwait(false))
            return "That command is for the Beatmap Nominators, NAT and admins.";
        if (services.GetService<BeatmapStatusChanger>() is not { } changer)
            return "Status changes need the website (osu-web mode).";
        if (caller.LastNowPlaying is not { } np)
            return "/np the map first.";

        var beatmap = await FindAsync(services, np, cancellationToken).ConfigureAwait(false);
        var setId = beatmap?.SetId ?? np.SetId;
        if (setId <= 0 || (!wholeSet && beatmap is null))
            return "I don't know that map. /np it from the song select or while playing.";
        IReadOnlyCollection<int>? only = wholeSet ? null : [beatmap!.Id];

        var result = status == BeatmapStatusChanger.UnlockName
            ? await changer.UnlockAsync(setId, only, caller.UserId, cancellationToken).ConfigureAwait(false)
            : await changer.ChangeAsync(setId, BeatmapStatusChanger.Statuses[status], only, caller.UserId, cancellationToken).ConfigureAwait(false);
        if (result is null)
            return "That set isn't on the server or on osu!.";

        var name = beatmap is null ? $"set {setId.ToString(CultureInfo.InvariantCulture)}" : $"{beatmap.Artist} - {beatmap.Title}";
        var target = status == BeatmapStatusChanger.UnlockName ? "bancho's status" : status;
        if (result.Changed.Count == 0)
        {
            return status == BeatmapStatusChanger.UnlockName
                ? $"Nothing to reset: {name} has no staff-set status, or osu! doesn't have it."
                : $"Nothing changed: {name} already has {target}.";
        }
        var which = wholeSet ? "all difficulties" : $"[{beatmap!.Version}]";
        var recalculated = result.Scores is { Changed: > 0 } scores
            ? string.Create(CultureInfo.InvariantCulture, $" ({scores.Changed} score(s) recalculated)")
            : string.Empty;
        return $"{name}: {which} → {target}{recalculated}.";
    }

    /// <summary>Administrators and developers, or members of the website's BN/NAT/admin groups.</summary>
    private static async Task<bool> MayChangeStatusAsync(IServiceProvider services, int userId, CancellationToken cancellationToken)
    {
        var user = await services.GetRequiredService<IUserRepository>().GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsRestricted)
            return false;
        if ((user.Privileges & (UserPrivileges.Administrator | UserPrivileges.Developer)) != 0)
            return true;
        return services.GetService<OsuWebCommunity>() is { } community
            && (await community.GetGroupsAsync(userId, cancellationToken).ConfigureAwait(false)).Overlaps(MapperGroups);
    }

    /// <summary>The /np'd difficulty (or the set's first one), from the server or, failing that, the osu! API.</summary>
    private async Task<Beatmap?> FindAsync(IServiceProvider services, NowPlaying np, CancellationToken cancellationToken)
    {
        var repository = services.GetRequiredService<IBeatmapRepository>();
        if (np.BeatmapId > 0 && await repository.GetByIdAsync(np.BeatmapId, cancellationToken).ConfigureAwait(false) is { } stored)
            return stored;

        if (np.SetId > 0)
        {
            var set = await repository.GetBySetAsync(np.SetId, cancellationToken).ConfigureAwait(false);
            if (set.Count == 0)
                set = await _beatmaps.FetchSetAsync(np.SetId, cancellationToken).ConfigureAwait(false);
            return np.BeatmapId > 0 ? set.FirstOrDefault(b => b.Id == np.BeatmapId) : set.OrderBy(b => b.StarRating).FirstOrDefault();
        }
        return null;
    }

    private static string StatusName(RankedStatus status) => status switch
    {
        RankedStatus.Ranked => "Ranked",
        RankedStatus.Approved => "Approved",
        RankedStatus.Qualified => "Qualified",
        RankedStatus.Loved => "Loved",
        _ => "Unranked",
    };

    /// <summary>"HDDT"-style acronyms (Nightcore and Perfect without the bits they imply).</summary>
    public static string ModNames(TomYum.Bancho.Mods mods)
    {
        if (mods.HasFlag(TomYum.Bancho.Mods.Nightcore))
            mods &= ~TomYum.Bancho.Mods.DoubleTime;
        if (mods.HasFlag(TomYum.Bancho.Mods.Perfect))
            mods &= ~TomYum.Bancho.Mods.SuddenDeath;
        (TomYum.Bancho.Mods Mod, string Acronym)[] names =
        [
            (TomYum.Bancho.Mods.NoFail, "NF"), (TomYum.Bancho.Mods.Easy, "EZ"), (TomYum.Bancho.Mods.Hidden, "HD"),
            (TomYum.Bancho.Mods.HardRock, "HR"), (TomYum.Bancho.Mods.SuddenDeath, "SD"), (TomYum.Bancho.Mods.Perfect, "PF"),
            (TomYum.Bancho.Mods.DoubleTime, "DT"), (TomYum.Bancho.Mods.Nightcore, "NC"), (TomYum.Bancho.Mods.HalfTime, "HT"),
            (TomYum.Bancho.Mods.Flashlight, "FL"), (TomYum.Bancho.Mods.SpunOut, "SO"), (TomYum.Bancho.Mods.Relax, "RX"),
            (TomYum.Bancho.Mods.Autopilot, "AP"), (TomYum.Bancho.Mods.FadeIn, "FI"), (TomYum.Bancho.Mods.Mirror, "MR"),
        ];
        return string.Concat(names.Where(n => mods.HasFlag(n.Mod)).Select(n => n.Acronym));
    }
}
