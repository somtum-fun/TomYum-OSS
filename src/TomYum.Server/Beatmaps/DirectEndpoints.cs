using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Server.Bancho;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// osu!Direct in-game beatmap browser endpoints. Proxies search and set-info requests to a
/// mino.pw-compatible beatmap mirror and redirects downloads to that mirror. All three
/// responses use the pipe-delimited plain-text format the osu!stable client expects.
/// Custom (uploaded) sets are served from this server instead (<see cref="CustomBeatmapStore"/>).
/// </summary>
public static partial class DirectEndpoints
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
        // "/d/123n" is the set without its video (builds from b699 on).
        app.MapGet("/d/{setId}", HandleDownloadForClientAsync);

        // One difficulty's .osu file, like osu.ppy.sh/osu/{id}: ppy's score processor reads maps
        // from here (BEATMAP_DOWNLOAD_PATH), so custom maps (whose files are only in TomYum's
        // .osu cache) get pp too. No host restriction: it's called by the compose service name.
        app.MapGet("/osu/{beatmapId:int}", HandleOsuFileAsync);

        // Custom sets' covers, where the website links them (Caddy sends these paths here; the
        // official sets' covers are on ppy's asset server).
        app.MapGet("/uploads/default/beatmaps/{setId:int}/covers/{file}", HandleCover);
        // …and their audio preview (the website's player; the fork links custom sets here).
        app.MapGet("/uploads/default/beatmaps/{setId:int}/preview.mp3", (int setId, CustomBeatmapStore store, HttpContext context) =>
        {
            if (setId < Beatmap.CustomIdFloor || store.PreviewPath(setId) is not { } path)
                return Results.NotFound();
            NoCache(context);
            return Results.File(path, "audio/mpeg", enableRangeProcessing: true);
        });
    }

    internal static IResult HandleCover(int setId, string file, CustomBeatmapStore store)
    {
        if (setId < Beatmap.CustomIdFloor || !file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            return Results.NotFound();
        var size = file[..^4];
        var path = size is "raw" or "fullsize" ? store.BackgroundPath(setId) : store.CoverPath(setId, size);
        return path is null ? Results.NotFound() : Results.File(path, ContentTypeOf(path));
    }

    private static string ContentTypeOf(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

    private static async Task<IResult> HandleOsuFileAsync(int beatmapId, TomYum.Performance.OsuFileCache files, CancellationToken cancellationToken)
    {
        if (beatmapId <= 0)
            return Results.NotFound();

        try
        {
            var bytes = await files.GetAsync(beatmapId, cancellationToken).ConfigureAwait(false);
            return Results.File(bytes, "text/plain; charset=utf-8", $"{beatmapId}.osu");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            // Neither cached (a custom map) nor on osu.ppy.sh.
            return Results.NotFound();
        }
    }

    /// <summary>
    /// Maps a catch-all proxy for the <c>b.</c> subdomain onto <paramref name="hosts"/>.
    /// osu!stable requests thumbnails (<c>/thumb/{id}[l].jpg</c>), audio previews
    /// (<c>/preview/{id}.mp3</c>), and potentially other beatmap assets from this host.
    /// We forward every path straight to ppy's CDN (<c>b.ppy.sh</c>), mirroring the
    /// approach used by bancho.py's <c>map.py</c> domain.
    /// </summary>
    public static void MapBeatmapAssets(this IEndpointRouteBuilder app, params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/{**path}", HandleBeatmapAsset)
            .RequireHost(hosts);
    }

    // ── /web/osu-search.php ────────────────────────────────────────────────────────────────

    internal static async Task<IResult> HandleSearchAsync(
        HttpContext context,
        IHttpClientFactory factory,
        IOptions<DirectOptions> opts,
        CustomBeatmapCatalog custom,
        PlayerRegistry players,
        CancellationToken cancellationToken)
    {
        var cfg = opts.Value;
        var withoutMania = HasNoMania(context.Request, players);

        // Query params the osu! client sends:
        //   r = status filter (0=ranked, 2=pending, 3=qualified, 4=all, 5=graveyard, 7=ranked (played), 8=loved)
        //   q = search text ("Newest", "Top Rated", "Most Played" are the tabs, not text; the
        //       client writes them with '+', which arrives here as a space)
        //   m = mode (-1=all, 0=osu!, 1=taiko, 2=catch, 3=mania)
        //   p = page number (0-indexed; 100 results per page)
        if (!int.TryParse(context.Request.Query["r"].ToString(), out var directStatus))
            directStatus = 4; // all
        var query = context.Request.Query["q"].ToString();
        var isTab = IsTab(query);
        if (!int.TryParse(context.Request.Query["m"].ToString(), out var mode))
            mode = -1;
        if (!int.TryParse(context.Request.Query["p"].ToString(), out var page))
            page = 0;

        var qs = new StringBuilder("?amount=100&offset=");
        qs.Append((page * 100).ToString(CultureInfo.InvariantCulture));

        if (!isTab)
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

        // Numbered builds (b1122) send no player (u/h): they get only the sets this server has, its
        // own and the osu! ones it has imported, not the mirror's (for now: what's here is known to
        // work there). They show one page of up to 100.
        if (!context.Request.Query.ContainsKey("u"))
        {
            IReadOnlyList<CustomSet> local = await custom.SearchAsync(isTab ? null : query, mode == -1 ? null : mode, mirrorStatus, 100, includeImported: true, cancellationToken).ConfigureAwait(false);
            if (withoutMania)
                local = local.Where(set => !IsManiaOnly(set.Maps.Select(b => b.Mode))).ToList();
            var localLines = new StringBuilder();
            localLines.AppendLine(local.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var set in local)
                localLines.AppendLine(custom.ToDirectLine(set));
            return Results.Text(localLines.ToString(), "text/plain");
        }

        var url = cfg.MirrorBaseUrl + cfg.SearchPath + qs;

        // This server's own sets come first, on the first page.
        IReadOnlyList<CustomSet> customSets = page == 0
            ? await custom.SearchAsync(isTab ? null : query, mode == -1 ? null : mode, mirrorStatus, 100, cancellationToken: cancellationToken).ConfigureAwait(false)
            : [];
        if (withoutMania)
            customSets = customSets.Where(set => !IsManiaOnly(set.Maps.Select(b => b.Mode))).ToList();

        JsonElement[] sets;
        try
        {
            using var http = factory.CreateClient(HttpClientName);
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return MirrorFailed(customSets, custom);

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            sets = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
        }
        catch (Exception)
        {
            return MirrorFailed(customSets, custom);
        }

        var lines = new StringBuilder();
        var shown = customSets.Count;
        foreach (var set in customSets)
            lines.AppendLine(custom.ToDirectLine(set));

        foreach (var set in sets)
        {
            if (!set.TryGetProperty("ChildrenBeatmaps", out var children) || children.ValueKind == JsonValueKind.Null)
                continue;
            if (withoutMania && IsManiaOnly(set))
                continue;
            shown++;

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
                    // :.2f is Python syntax; the C# equivalent is :F2.
                    return string.Create(CultureInfo.InvariantCulture,
                        $"[{sr:F2}⭐] {name} {{cs: {cs} / od: {od} / ar: {ar} / hp: {hp}}}@{m}");
                });

            var setId   = set.TryGetProperty("SetID",        out var sid)  ? sid.GetInt32()    : 0;
            var artist  = SanitisePipeChar(set.TryGetProperty("Artist",       out var art)  ? art.GetString() ?? "" : "");
            var title   = SanitisePipeChar(set.TryGetProperty("Title",        out var tit)  ? tit.GetString() ?? "" : "");
            var creator = SanitisePipeChar(set.TryGetProperty("Creator", out var cre) ? cre.GetString() ?? "" : "");
            // Pass the mirror's RankedStatus value through as-is. bancho.py sends it raw and
            // the osu!stable client renders the icon from this field directly (1=ranked,
            // 4=loved, 3=qualified, 0=pending, -2=graveyard). ToDirectStatus() was incorrectly
            // remapping it to the *filter* codes (r= query param), which is a different domain.
            var ranked  = set.TryGetProperty("RankedStatus", out var rs) ? rs.GetInt32() : 0;
            var updated = set.TryGetProperty("LastUpdate",   out var lu)   ? lu.GetString() ?? "" : "";
            var hasVideo = set.TryGetProperty("HasVideo",    out var hv)
                ? (hv.ValueKind == JsonValueKind.True ? 1 : hv.ValueKind == JsonValueKind.False ? 0 : hv.GetInt32())
                : 0;

            // Wire format (bancho.py DIRECT_SET_INFO_FMTSTR):
            // {SetID}.osz|{Artist}|{Title}|{Creator}|{RankedStatus}|10.0|{LastUpdate}|{SetID}|
            // 0|{HasVideo}|0|0|0|{diffs}
            lines.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{setId}.osz|{artist}|{title}|{creator}|{ranked}|10.0|{updated}|{setId}|0|{hasVideo}|0|0|0|{string.Join(",", diffs)}"));
        }

        // If exactly 100 results are returned, signal "more pages available" by sending 101.
        var sb = new StringBuilder();
        sb.AppendLine(sets.Length == 100 ? "101" : shown.ToString(CultureInfo.InvariantCulture));
        sb.Append(lines);
        return Results.Text(sb.ToString(), "text/plain");
    }

    /// <summary>The mirror is down: this server's own sets still show, else the client's error.</summary>
    private static IResult MirrorFailed(IReadOnlyList<CustomSet> customSets, CustomBeatmapCatalog custom)
    {
        if (customSets.Count == 0)
            return Results.Text("-1\nFailed to retrieve beatmaps from the mirror.", "text/plain");
        var sb = new StringBuilder();
        sb.AppendLine(customSets.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var set in customSets)
            sb.AppendLine(custom.ToDirectLine(set));
        return Results.Text(sb.ToString(), "text/plain");
    }

    // ── /web/osu-search-set.php ───────────────────────────────────────────────────────────

    internal static async Task<IResult> HandleSearchSetAsync(
        HttpContext context,
        IHttpClientFactory factory,
        IOptions<DirectOptions> opts,
        CustomBeatmapCatalog custom,
        PlayerRegistry players,
        CancellationToken cancellationToken)
    {
        var cfg = opts.Value;
        // A build without osu!mania can't play a mania-only set: for it, there's no such set.
        var withoutMania = HasNoMania(context.Request, players);

        // Client sends exactly one of: s=<setId>, b=<mapId>, c=<md5>
        var setIdStr  = context.Request.Query["s"].ToString();
        var mapIdStr  = context.Request.Query["b"].ToString();
        var checksum  = context.Request.Query["c"].ToString();

        // This server's own sets: by id, or by checksum when the mirror wouldn't know it.
        int? customSet = int.TryParse(setIdStr, CultureInfo.InvariantCulture, out var s) && s >= Beatmap.CustomIdFloor ? s : null;
        int? customMap = int.TryParse(mapIdStr, CultureInfo.InvariantCulture, out var b) && b >= Beatmap.CustomIdFloor ? b : null;
        var byChecksum = setIdStr.Length == 0 && mapIdStr.Length == 0 && checksum.Length == 32 ? checksum : null;
        if (customSet is not null || customMap is not null || byChecksum is not null)
        {
            if (await custom.FindAsync(customSet, customMap, byChecksum, cancellationToken).ConfigureAwait(false) is { } local)
            {
                return withoutMania && IsManiaOnly(local.Maps.Select(b => b.Mode))
                    ? Results.Text(string.Empty, "text/plain")
                    : Results.Text(custom.ToDirectLine(local, withDifficulties: false), "text/plain");
            }
            if (byChecksum is null)
                return Results.Text(string.Empty, "text/plain");
        }

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

        if (withoutMania && IsManiaOnly(set))
            return Results.Text(string.Empty, "text/plain");

        var sid     = set.TryGetProperty("SetID",        out var sidp) ? sidp.GetInt32()    : 0;
        var artist  = SanitisePipeChar(set.TryGetProperty("Artist",  out var art) ? art.GetString() ?? "" : "");
        var title   = SanitisePipeChar(set.TryGetProperty("Title",   out var tit) ? tit.GetString() ?? "" : "");
        var creator = SanitisePipeChar(set.TryGetProperty("Creator", out var cre) ? cre.GetString() ?? "" : "");
        // The mirror's status as it is (1 ranked, 2 approved, 3 qualified, 4 loved), as in osu-search.php;
        // not the search filter's codes (ranked is 7 there, which every old build shows as pending).
        var status  = set.TryGetProperty("RankedStatus", out var rs) ? rs.GetInt32() : 0;
        var updated = set.TryGetProperty("LastUpdate",   out var lu)  ? lu.GetString() ?? "" : "";

        // Wire format (bancho.py osu-search-set response):
        // {set_id}.osz|{artist}|{title}|{creator}|{status}|10.0|{last_update}|{set_id}|0|0|0|0|0
        return Results.Text(
            string.Create(CultureInfo.InvariantCulture,
                $"{sid}.osz|{artist}|{title}|{creator}|{status}|10.0|{updated}|{sid}|0|0|0|0|0"),
            "text/plain");
    }

    // ── /d/{setId} ────────────────────────────────────────────────────────────────────────

    /// <summary>Named HttpClient for downloads passed through to old clients (a whole .osz takes a while).</summary>
    internal const string DownloadHttpClientName = "BeatmapMirrorDownload";

    /// <summary>
    /// A download for whoever asks (<c>/d/{set}</c>, or <c>/d/{set}n</c> without the video; <c>?u=</c> is the
    /// player when it's sent): <see cref="HandleDownload"/>, except that an old build (before 2016) can't
    /// follow a redirect to the mirror's HTTPS (no TLS 1.2 on .NET 2.0), so the file is fetched here and
    /// passed on. Old builds are told by the plain HTTP they come over (b476–b639 send no <c>?u=</c>, and
    /// b452's is malformed), or by the online player's build.
    /// </summary>
    internal static async Task<IResult> HandleDownloadForClientAsync(
        string setId,
        HttpContext context,
        IOptions<DirectOptions> opts,
        CustomBeatmapStore store,
        PlayerRegistry players,
        IHttpClientFactory factory,
        CancellationToken cancellationToken)
    {
        if (ParseDownloadId(setId) is not { } parsed)
            return Results.NotFound();
        var (id, noVideo) = parsed;

        var result = HandleDownload(id, context.Request, opts, store, noVideo);
        if (result is not RedirectHttpResult redirect
            || !(CameOverPlainHttp(context.Request)
                 || (context.Request.Query["u"].ToString() is { Length: > 0 } username && players.GetByUsername(username) is { Build.IsLegacy: true })))
        {
            return result;
        }

        var http = factory.CreateClient(DownloadHttpClientName);
        var upstream = await http.GetAsync(redirect.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!upstream.IsSuccessStatusCode)
        {
            var status = (int)upstream.StatusCode;
            upstream.Dispose();
            return Results.StatusCode(status);
        }

        context.Response.RegisterForDispose(upstream);
        context.Response.ContentLength = upstream.Content.Headers.ContentLength; // the client's progress bar
        var name = upstream.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"')
            ?? upstream.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? string.Create(CultureInfo.InvariantCulture, $"{id}.osz");
        var body = await upstream.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return Results.Stream(body, "application/x-osu-beatmap-archive", name);
    }

    /// <summary>The set id in a <c>/d/</c> path, and whether a trailing <c>n</c> asks for it without the video; null when it isn't one.</summary>
    internal static (int SetId, bool NoVideo)? ParseDownloadId(string value) =>
        DownloadId().Match(value ?? string.Empty) is { Success: true } match
        && int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? (id, match.Groups["n"].Success)
            : null;

    internal static IResult HandleDownload(
        int setId,
        HttpRequest request,
        IOptions<DirectOptions> opts,
        CustomBeatmapStore store,
        bool noVideo = false)
    {
        if (setId >= Beatmap.CustomIdFloor)
        {
            return store.HasSet(setId)
                ? Results.File(store.OszPath(setId), "application/x-osu-beatmap-archive", string.Create(CultureInfo.InvariantCulture, $"{setId}.osz"))
                : Results.NotFound();
        }

        var cfg = opts.Value;
        // Pass through ?novideo if the client sent it; osu-web (as a download mirror) sends nv=1.
        var withoutVideo = noVideo || request.Query.ContainsKey("novideo") || request.Query["nv"] == "1" ? "?novideo" : string.Empty;
        var url = cfg.MirrorBaseUrl
            + string.Format(CultureInfo.InvariantCulture, cfg.DownloadPath, setId)
            + withoutVideo;
        return Results.Redirect(url, permanent: false);
    }

    // ── b.{domain} catch-all ──────────────────────────────────────────────────────────────

    internal static async Task<IResult> HandleBeatmapAsset(
        HttpContext context, IOptions<DirectOptions> opts, CustomBeatmapStore store, AudioPreviews previews, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var local = CustomAsset().Match(request.Path.Value ?? string.Empty);
        if (local.Success && int.TryParse(local.Groups["id"].Value, CultureInfo.InvariantCulture, out var setId))
        {
            var preview = local.Groups["kind"].Value == "preview";

            // Custom sets' thumbnails and previews are this server's.
            if (setId >= Beatmap.CustomIdFloor)
            {
                var path = preview ? store.PreviewPath(setId) : store.CoverPath(setId, local.Groups["large"].Success ? "thumbl" : "thumb");
                if (path is null)
                    return Results.NotFound();
                if (preview)
                    NoCache(context);
                return Results.File(path, preview ? "audio/mpeg" : "image/jpeg", enableRangeProcessing: preview);
            }

            // osu! sets' previews are kept here too (AudioPreviews), so players see their length.
            if (preview)
            {
                if (await previews.GetAsync(setId, cancellationToken).ConfigureAwait(false) is not { } file)
                    return Results.NotFound();
                context.Response.Headers.CacheControl = "public, max-age=604800";
                return Results.File(file.Path, file.Type, enableRangeProcessing: true);
            }
        }

        // Forward the path verbatim to ppy's CDN — covers /thumb/{id}[l].jpg,
        // /preview/{id}.mp3, and anything else the client may request from b.*.
        // bancho.py map.py uses the same strategy: redirect f"https://b.ppy.sh{path}".
        var url = opts.Value.ThumbnailBaseUrl + request.Path;
        if (!CameOverPlainHttp(request))
            return Results.Redirect(url, permanent: true);

        // An old client (plain HTTP, no TLS 1.2: docs/old-clients.md) can't follow that: fetched here.
        var http = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
        using var upstream = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!upstream.IsSuccessStatusCode)
            return Results.StatusCode((int)upstream.StatusCode);
        var bytes = await upstream.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        context.Response.Headers.CacheControl = "public, max-age=86400";
        return Results.Bytes(bytes, upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream");
    }

    /// <summary>
    /// Whether the client reached the server over plain HTTP: only old builds do (today's use HTTPS).
    /// Behind Cloudflare its <c>Cf-Visitor</c> header has the scheme the client used (the tunnel and
    /// Caddy in between speak HTTP); otherwise the proxy's <c>X-Forwarded-Proto</c>. Without either
    /// (no proxy in front, which TomYum always has), it counts as HTTPS: today's client.
    /// </summary>
    internal static bool CameOverPlainHttp(HttpRequest request)
    {
        var visitor = request.Headers["Cf-Visitor"].ToString();
        if (visitor.Length > 0)
            return visitor.Contains("\"http\"", StringComparison.OrdinalIgnoreCase);
        var forwarded = request.Headers["X-Forwarded-Proto"].ToString();
        return forwarded.Length > 0 && string.Equals(forwarded.Split(',')[0].Trim(), "http", StringComparison.OrdinalIgnoreCase);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Replace <c>|</c> with <c>I</c> inside artist/title/diff name strings.
    /// The osu! client uses <c>|</c> as a field delimiter, so a literal pipe in a name
    /// causes the set line to be silently ignored. bancho.py applies the same fix.
    /// </summary>
    private static string SanitisePipeChar(string s) => s.Replace("|", "I");

    // Custom sets' previews change when their sets are uploaded again: Cloudflare (and browsers) ask each time
    // instead of keeping an old one (it kept a whole song for hours after its preview was cut).
    private static void NoCache(HttpContext context) => context.Response.Headers.CacheControl = "no-cache";

    [GeneratedRegex(@"^/(?<kind>thumb|preview)/(?<id>[0-9]+)(?<large>l)?\.(jpg|mp3)$")]
    private static partial Regex CustomAsset();

    [GeneratedRegex(@"^(?<id>[0-9]{1,9})(?<n>n)?$")]
    private static partial Regex DownloadId();

    /// <summary>
    /// The build asking, as far as osu!direct can tell: the online player named by <c>u</c> (sent from
    /// b1652 on), else the version the request carries (<c>v</c>). Null when neither says.
    /// </summary>
    internal static ClientBuild? ClientOf(HttpRequest request, PlayerRegistry players)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(players);
        if (request.Query["u"].ToString() is { Length: > 0 } username && players.GetByUsername(username) is { } player)
            return player.Build;
        return request.Query["v"].ToString() is { Length: > 0 } version ? ClientBuild.Parse(version) : null;
    }

    /// <summary>
    /// Whether the build asking has no osu!mania (numbered builds and those before b20121003). Builds
    /// before b1652 send no player at all; such a request over plain HTTP is one of them.
    /// </summary>
    internal static bool HasNoMania(HttpRequest request, PlayerRegistry players) =>
        ClientOf(request, players) is { } build
            ? !build.HasMania
            : !request.Query.ContainsKey("u") && CameOverPlainHttp(request);

    /// <summary>
    /// Whether every difficulty is osu!mania. Sets that mix modes stay listed for builds without mania:
    /// their mania difficulties show as empty "(mania)" maps there.
    /// </summary>
    internal static bool IsManiaOnly(IEnumerable<int> modes)
    {
        var any = false;
        foreach (var mode in modes)
        {
            if (mode != 3)
                return false;
            any = true;
        }
        return any;
    }

    /// <summary>Whether a mirror set (its <c>ChildrenBeatmaps</c>) is osu!mania only.</summary>
    internal static bool IsManiaOnly(JsonElement set) =>
        set.TryGetProperty("ChildrenBeatmaps", out var children) && children.ValueKind == JsonValueKind.Array
        && IsManiaOnly(children.EnumerateArray().Select(d => d.TryGetProperty("Mode", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0));

    /// <summary>The osu!direct tabs the client sends as its search text.</summary>
    internal static bool IsTab(string query) =>
        query.Replace('+', ' ').Trim() is "Newest" or "Top Rated" or "Most Played";

    /// <summary>The client's status filter as the mirror's status (osu!'s: 1 ranked, 0 pending, …); null: all.</summary>
    internal static int? ToMirrorStatus(int directStatus) => directStatus switch
    {
        0 => 1,    // Ranked
        2 => 0,    // Pending
        3 => 3,    // Qualified
        4 => null, // All: no filter
        5 => -2,   // Graveyard
        7 => 1,    // Ranked (played): the mirror can't tell what the player played, so ranked
        8 => 4,    // Loved
        _ => null,
    };
}
