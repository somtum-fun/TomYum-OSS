using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// osu!Direct in-game beatmap browser endpoints. Proxies search and set-info requests to a
/// mino.pw-compatible beatmap mirror and redirects downloads to that mirror. All three
/// responses use the pipe-delimited plain-text format the osu!stable client expects.
/// </summary>
public static class DirectEndpoints
{
    // Named HttpClient key used throughout this class.
    internal const string HttpClientName = "BeatmapMirror";

    public static void MapDirect(this IEndpointRouteBuilder app, params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/web/osu-search.php", HandleSearchAsync)
            .RequireHost(hosts);

        app.MapGet("/web/osu-search-set.php", HandleSearchSetAsync)
            .RequireHost(hosts);

        // Download redirect — no host restriction so clients that hit the osu. subdomain
        // for /d/ still get the redirect. If the deployment needs host filtering, add hosts.
        app.MapGet("/d/{setId}", HandleDownload);
    }

    // ── /web/osu-search.php ────────────────────────────────────────────────────────────────

    private static async Task<IResult> HandleSearchAsync(
        HttpContext context,
        IHttpClientFactory factory,
        IOptions<DirectOptions> opts,
        CancellationToken cancellationToken)
    {
        var cfg = opts.Value;

        // Query params the osu! client sends:
        //   r = ranked filter (0=pending,2=pending,3=qualified,4=all,5=graveyard,7=ranked,8=loved)
        //   q = search text  ("Newest"/"Top+Rated"/"Most+Played" are special sort keys, not text)
        //   m = mode (-1=all, 0=osu!, 1=taiko, 2=catch, 3=mania)
        //   p = page number (0-indexed; 100 results per page)
        if (!int.TryParse(context.Request.Query["r"].ToString(), out var directStatus))
            directStatus = 4; // all
        var query = context.Request.Query["q"].ToString();
        if (!int.TryParse(context.Request.Query["m"].ToString(), out var mode))
            mode = -1;
        if (!int.TryParse(context.Request.Query["p"].ToString(), out var page))
            page = 0;

        var qs = new StringBuilder("?amount=100&offset=");
        qs.Append((page * 100).ToString(CultureInfo.InvariantCulture));

        if (query is not ("Newest" or "Top+Rated" or "Most+Played"))
        {
            qs.Append("&query=");
            qs.Append(Uri.EscapeDataString(query));
        }

        if (mode != -1)
        {
            qs.Append("&mode=");
            qs.Append(mode.ToString(CultureInfo.InvariantCulture));
        }

        var mirrorStatus = ToMirrorStatus(directStatus);
        if (mirrorStatus is not null)
        {
            qs.Append("&status=");
            qs.Append(mirrorStatus.Value.ToString(CultureInfo.InvariantCulture));
        }

        var url = cfg.MirrorBaseUrl + cfg.SearchPath + qs;

        JsonElement[] sets;
        try
        {
            using var http = factory.CreateClient(HttpClientName);
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Results.Text("-1\nFailed to retrieve beatmaps from the mirror.", "text/plain");

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            sets = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
        }
        catch (Exception)
        {
            return Results.Text("-1\nFailed to retrieve beatmaps from the mirror.", "text/plain");
        }

        var sb = new StringBuilder();
        // If exactly 100 results are returned, signal "more pages available" by sending 101.
        sb.AppendLine(sets.Length == 100 ? "101" : sets.Length.ToString(CultureInfo.InvariantCulture));

        foreach (var set in sets)
        {
            if (!set.TryGetProperty("ChildrenBeatmaps", out var children) || children.ValueKind == JsonValueKind.Null)
                continue;

            var diffs = children.EnumerateArray()
                .OrderBy(d => d.TryGetProperty("DifficultyRating", out var dr) ? dr.GetDouble() : 0)
                .Select(d =>
                {
                    var sr = d.TryGetProperty("DifficultyRating", out var drp) ? drp.GetDouble() : 0.0;
                    var name = SanitisePipeChar(d.TryGetProperty("DiffName", out var dn) ? dn.GetString() ?? "" : "");
                    var cs = d.TryGetProperty("CS", out var csp) ? csp.GetDouble() : 0.0;
                    var od = d.TryGetProperty("OD", out var odp) ? odp.GetDouble() : 0.0;
                    var ar = d.TryGetProperty("AR", out var arp) ? arp.GetDouble() : 0.0;
                    var hp = d.TryGetProperty("HP", out var hpp) ? hpp.GetDouble() : 0.0;
                    var m = d.TryGetProperty("Mode", out var mp) ? mp.GetInt32() : 0;
                    return string.Create(CultureInfo.InvariantCulture,
                        $"[{sr:.2f}⭐] {name} {{cs: {cs} / od: {od} / ar: {ar} / hp: {hp}}}@{m}");
                });

            var setId   = set.TryGetProperty("SetID",        out var sid)  ? sid.GetInt32()    : 0;
            var artist  = SanitisePipeChar(set.TryGetProperty("Artist",       out var art)  ? art.GetString() ?? "" : "");
            var title   = SanitisePipeChar(set.TryGetProperty("Title",        out var tit)  ? tit.GetString() ?? "" : "");
            var creator = set.TryGetProperty("Creator",      out var cre)  ? cre.GetString() ?? "" : "";
            var ranked  = set.TryGetProperty("RankedStatus", out var rs)   ? rs.GetInt32()    : 0;
            var updated = set.TryGetProperty("LastUpdate",   out var lu)   ? lu.GetString() ?? "" : "";
            var hasVideo = set.TryGetProperty("HasVideo",    out var hv)
                ? (hv.ValueKind == JsonValueKind.True ? 1 : hv.ValueKind == JsonValueKind.False ? 0 : hv.GetInt32())
                : 0;

            // Wire format (bancho.py DIRECT_SET_INFO_FMTSTR):
            // {SetID}.osz|{Artist}|{Title}|{Creator}|{RankedStatus}|10.0|{LastUpdate}|{SetID}|
            // 0|{HasVideo}|0|0|0|{diffs}
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{setId}.osz|{artist}|{title}|{creator}|{ranked}|10.0|{updated}|{setId}|0|{hasVideo}|0|0|0|{string.Join(",", diffs)}"));
        }

        return Results.Text(sb.ToString(), "text/plain");
    }

    // ── /web/osu-search-set.php ───────────────────────────────────────────────────────────

    private static async Task<IResult> HandleSearchSetAsync(
        HttpContext context,
        IHttpClientFactory factory,
        IOptions<DirectOptions> opts,
        CancellationToken cancellationToken)
    {
        var cfg = opts.Value;

        // Client sends exactly one of: s=<setId>, b=<mapId>, c=<md5>
        var setIdStr  = context.Request.Query["s"].ToString();
        var mapIdStr  = context.Request.Query["b"].ToString();
        var checksum  = context.Request.Query["c"].ToString();

        string mirrorUrl;
        if (!string.IsNullOrEmpty(setIdStr) && int.TryParse(setIdStr, out var setId))
            mirrorUrl = cfg.MirrorBaseUrl + string.Format(CultureInfo.InvariantCulture, cfg.SetPath, setId);
        else if (!string.IsNullOrEmpty(mapIdStr) && int.TryParse(mapIdStr, out var mapId))
            mirrorUrl = cfg.MirrorBaseUrl + string.Format(CultureInfo.InvariantCulture, cfg.SetPath, mapId) + "?type=b";
        else if (!string.IsNullOrEmpty(checksum))
            mirrorUrl = cfg.MirrorBaseUrl + string.Format(CultureInfo.InvariantCulture, cfg.SetPath, checksum) + "?type=c";
        else
            return Results.Text(string.Empty, "text/plain");

        JsonElement set;
        try
        {
            using var http = factory.CreateClient(HttpClientName);
            using var response = await http.GetAsync(mirrorUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Results.Text(string.Empty, "text/plain");

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            set = doc.RootElement.Clone();
        }
        catch (Exception)
        {
            return Results.Text(string.Empty, "text/plain");
        }

        var sid     = set.TryGetProperty("SetID",        out var sidp) ? sidp.GetInt32()    : 0;
        var artist  = SanitisePipeChar(set.TryGetProperty("Artist",  out var art) ? art.GetString() ?? "" : "");
        var title   = SanitisePipeChar(set.TryGetProperty("Title",   out var tit) ? tit.GetString() ?? "" : "");
        var creator = set.TryGetProperty("Creator",      out var cre) ? cre.GetString() ?? "" : "";
        var status  = set.TryGetProperty("RankedStatus", out var rs)  ? rs.GetInt32()    : 0;
        var updated = set.TryGetProperty("LastUpdate",   out var lu)  ? lu.GetString() ?? "" : "";

        // Wire format (bancho.py osu-search-set response):
        // {set_id}.osz|{artist}|{title}|{creator}|{status}|10.0|{last_update}|{set_id}|0|0|0|0|0
        return Results.Text(
            string.Create(CultureInfo.InvariantCulture,
                $"{sid}.osz|{artist}|{title}|{creator}|{status}|10.0|{updated}|{sid}|0|0|0|0|0"),
            "text/plain");
    }

    // ── /d/{setId} ────────────────────────────────────────────────────────────────────────

    private static IResult HandleDownload(
        int setId,
        HttpRequest request,
        IOptions<DirectOptions> opts)
    {
        var cfg = opts.Value;
        // Pass through ?novideo if the client sent it.
        var noVideo = request.Query.ContainsKey("novideo") ? "?novideo" : string.Empty;
        var url = cfg.MirrorBaseUrl
            + string.Format(CultureInfo.InvariantCulture, cfg.DownloadPath, setId)
            + noVideo;
        return Results.Redirect(url, permanent: false);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Replace <c>|</c> with <c>I</c> inside artist/title/diff name strings.
    /// The osu! client uses <c>|</c> as a field delimiter, so a literal pipe in a name
    /// causes the set line to be silently ignored. bancho.py applies the same fix.
    /// </summary>
    private static string SanitisePipeChar(string s) => s.Replace("|", "I");

    /// <summary>
    /// Maps the osu!Direct ranked-status filter code (sent by the client as <c>?r=</c>)
    /// to the osu! API status value expected by mino.pw-compatible mirrors.
    /// Returns <c>null</c> for "all" (no status filter should be sent).
    /// </summary>
    private static int? ToMirrorStatus(int directStatus) => directStatus switch
    {
        0 => 0,   // Pending
        2 => 0,   // Pending (alternate code)
        3 => 3,   // Qualified
        4 => null, // All — omit the filter
        5 => -2,  // Graveyard
        7 => 1,   // Ranked
        8 => 4,   // Loved
        _ => null,
    };
}
