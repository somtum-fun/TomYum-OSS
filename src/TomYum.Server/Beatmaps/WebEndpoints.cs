using System.Globalization;
using TomYum.Domain.Enums;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// Maps the osu!stable <c>/web/*.php</c> surface served from the <c>osu.</c> host. This slice
/// implements <c>osu-osz2-getscores.php</c> at the <em>status-line</em> level only: it resolves the
/// beatmap by MD5 and returns the leaderboard header bancho.py emits, so the client shows the
/// correct ranked status. Leaderboard score rows and score submission are later slices.
/// </summary>
/// <remarks>
/// Response line 1 (bancho.py <c>osu.py</c> ~line 982):
/// <c>{ranked_status}|{server_has_osz2:false}|{map_id}|{set_id}|{n_scores}|{fa_track_id}|{fa_license}</c>.
/// The status integer is the internal <see cref="RankedStatus"/> value written verbatim. When the
/// map is below <see cref="RankedStatus.Ranked"/> (or unknown), the client only needs
/// <c>{status}|false</c> with no leaderboard — matching bancho.py's early-out.
/// </remarks>
public static class WebEndpoints
{
    /// <summary>
    /// Registers the <c>/web/*</c> handlers, constrained to the <c>osu.</c> host (plus localhost
    /// for development). The host patterns come from <see cref="Bancho.BanchoHosts.Web"/>.
    /// </summary>
    public static void MapWeb(this IEndpointRouteBuilder app, params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/web/osu-osz2-getscores.php", HandleGetScoresAsync)
            .RequireHost(hosts);
    }

    private static async Task<IResult> HandleGetScoresAsync(
        HttpContext context,
        BeatmapService beatmaps,
        CancellationToken cancellationToken)
    {
        // The client passes the beatmap hash as `c` and the filename as `f`; we only need the hash
        // for the status line. Other params (mods, mode, set id) matter once we serve scores.
        var md5 = context.Request.Query["c"].ToString();
        if (string.IsNullOrEmpty(md5))
        {
            // No hash: nothing to resolve. Report not-submitted so the client stops asking.
            return Results.Text(NotSubmitted, "text/plain");
        }

        var beatmap = await beatmaps.GetByMd5Async(md5, cancellationToken).ConfigureAwait(false);
        if (beatmap is null)
        {
            return Results.Text(NotSubmitted, "text/plain");
        }

        // Below Ranked there is no leaderboard — the client only needs "{status}|false".
        if (!beatmap.Status.HasLeaderboard())
        {
            var line = string.Create(CultureInfo.InvariantCulture, $"{(int)beatmap.Status}|false");
            return Results.Text(line, "text/plain");
        }

        // Ranked/Approved/Qualified/Loved: emit the full header (no score rows yet).
        // {status}|{has_osz2}|{map_id}|{set_id}|{n_scores}|{fa_track_id}|{fa_license}
        // followed by offset, beatmap name, and rating lines, then an empty personal-best line.
        var header = string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)beatmap.Status}|false|{beatmap.Id}|{beatmap.SetId}|0|0|\n0\n{beatmap.FullName}\n10.0\n\n");
        return Results.Text(header, "text/plain");
    }

    /// <summary>The not-submitted status line: <c>-1|false</c>.</summary>
    private static readonly string NotSubmitted =
        string.Create(CultureInfo.InvariantCulture, $"{(int)RankedStatus.NotSubmitted}|false");
}
