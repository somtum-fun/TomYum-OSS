using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Server.Beatmaps.Osz2;

namespace TomYum.Server.Beatmaps;

/// <summary>Body of lazer's <c>PUT /beatmapsets</c>.</summary>
public sealed record LazerPutBeatmapSet(
    [property: JsonPropertyName("beatmapset_id")] int? BeatmapsetId,
    [property: JsonPropertyName("beatmaps_to_create")] int BeatmapsToCreate,
    [property: JsonPropertyName("beatmaps_to_keep")] IReadOnlyList<int>? BeatmapsToKeep,
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("notify_on_discussion_replies")] bool NotifyOnDiscussionReplies);

/// <summary>
/// Beatmap submission from the game clients (<see cref="BeatmapSubmissions"/>): the stable editor's
/// <c>/web/osu-osz2-bmsubmit-*.php</c> (an encrypted <c>.osz2</c>), and lazer's submission service,
/// which lazer finds at <c>{API URL}/beatmap-submission</c> unless the player sets another.
/// </summary>
public static class SubmissionEndpoints
{
    /// <summary>Where lazer's submission requests go (under the website's host).</summary>
    public const string LazerPrefix = "/beatmap-submission";

    private const long MaxUploadBytes = CustomBeatmapImporter.MaxOszBytes + 1024 * 1024;

    public static void MapSubmission(this IEndpointRouteBuilder app, params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(app);

        // osu!stable.
        app.MapGet("/web/osu-osz2-bmsubmit-getid.php", StableGetIdAsync).RequireHost(hosts);
        app.MapPost("/web/osu-osz2-bmsubmit-upload.php", StableUploadAsync).RequireHost(hosts)
            .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes));
        app.MapPost("/web/osu-osz2-bmsubmit-post.php", StablePostAsync).RequireHost(hosts);
        // The description of a set being updated; without it the editor starts from its default text.
        app.MapGet("/web/osu-get-beatmap-topic.php", () => Results.Text("1\n", "text/plain")).RequireHost(hosts);

        // osu!(lazer).
        var lazer = app.MapGroup(LazerPrefix).RequireHost(hosts);
        lazer.MapPut("/beatmapsets", LazerPutAsync);
        lazer.MapPut("/beatmapsets/{setId:int}", LazerReplaceAsync).WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes));
        lazer.MapPatch("/beatmapsets/{setId:int}", LazerPatchAsync).WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes));
        lazer.MapPatch("/beatmapsets/{setId:int}/beatmaps/{beatmapId:int}", () =>
            LazerError(new SubmissionException(422, "Guest difficulties can't be uploaded on their own on this server; ask the set's owner to include yours.")));
    }

    // ── osu!stable ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>GET osu-osz2-bmsubmit-getid.php?u=&amp;h=&amp;s=&amp;b=&amp;z=</c>: ids for the set (<c>s</c>, -1 for
    /// a new one) and its difficulties (<c>b</c>, the ids in the editor's files, in its order).
    /// Answers <c>0\n{set}\n{ids}\n{1 = send the full package}\n{sets left}\n{0}</c>, or an error code and message.
    /// </summary>
    internal static async Task<IResult> StableGetIdAsync(HttpContext context, BeatmapSubmissions submissions, IBeatmapRepository beatmaps, CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        if (await StableUserAsync(context, query["u"], query["h"], cancellationToken).ConfigureAwait(false) is not { } user)
            return StableError(5, "Authentication failed. Please check your username and password and try again!");

        var wanted = query["b"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(b => int.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : (int?)null)
            .ToList();
        _ = int.TryParse(query["s"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var setId);

        int? set = null;
        if (setId >= Beatmap.CustomIdFloor)
        {
            set = setId;
        }
        else if (setId > 0)
        {
            // An osu! set (the editor was working on a downloaded map): it belongs to its mapper there.
            return StableError(1, "The beatmap you're trying to submit isn't owned by you.");
        }
        else
        {
            // A set the editor lost the id of: its difficulties still know theirs.
            foreach (var id in wanted.OfType<int>().Where(id => id >= Beatmap.CustomIdFloor))
            {
                if (await beatmaps.GetByIdAsync(id, cancellationToken).ConfigureAwait(false) is { } known)
                {
                    set = known.SetId;
                    break;
                }
            }
        }

        try
        {
            var ids = await submissions.ReserveAsync(user, set, wanted, target: null, strictIds: false, cancellationToken).ConfigureAwait(false);
            return Results.Text(string.Create(CultureInfo.InvariantCulture,
                $"0\n{ids.SetId}\n{string.Join(',', ids.BeatmapIds)}\n1\n{ids.Remaining}\n0"), "text/plain");
        }
        catch (SubmissionException ex)
        {
            return StableError(ex);
        }
    }

    /// <summary><c>POST osu-osz2-bmsubmit-upload.php</c>: the <c>.osz2</c> package (always in full: getid asks for that).</summary>
    internal static async Task<IResult> StableUploadAsync(HttpContext context, BeatmapSubmissions submissions, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var form = request.HasFormContentType ? await request.ReadFormAsync(cancellationToken).ConfigureAwait(false) : null;
        string Field(string name) => form?[name].ToString() is { Length: > 0 } value ? value : request.Query[name].ToString();

        if (await StableUserAsync(context, Field("u"), Field("h"), cancellationToken).ConfigureAwait(false) is not { } user)
            return StableError(5, "Authentication failed. Please check your username and password and try again!");
        if (!int.TryParse(Field("s"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var setId))
            return StableError(5, "The beatmap set you're uploading to doesn't exist. Please try again!");
        if (Field("t") == "0")
            return StableError(5, "Please submit the beatmap again (this server takes full uploads only).");
        // The editor names the file field "0" (older ones "osz2"): take whichever came.
        if ((form?.Files is { Count: > 0 } files ? files[0] : null) is not { Length: > 0 } file)
            return StableError(5, "Nothing was uploaded. Please try again!");
        if (file.Length > CustomBeatmapImporter.MaxOszBytes)
            return StableError(5, "Your beatmap is too big. Try to reduce its filesize and try again!");

        var bytes = await ReadAllAsync(file, cancellationToken).ConfigureAwait(false);
        // The editor sends the package's MD5: a transfer that went wrong shows here.
        if (Field("z") is { Length: 32 } expected && !expected.Equals(Md5(bytes), StringComparison.OrdinalIgnoreCase))
            return StableError(5, "The upload was damaged on the way. Please try again!");

        byte[] osz;
        try
        {
            // Which set and ids its difficulties have is checked on their files, like any submission.
            osz = Osz2Package.Read(bytes).ToOsz();
        }
        catch (InvalidDataException ex)
        {
            return StableError(5, ex.Message);
        }

        try
        {
            await submissions.UploadAsync(user, setId, osz, "osu!stable", cancellationToken).ConfigureAwait(false);
            return Results.Text("0", "text/plain");
        }
        catch (SubmissionException ex)
        {
            return StableError(ex);
        }
    }

    /// <summary>
    /// <c>POST osu-osz2-bmsubmit-post.php</c> (form <c>u, p, b, subject, message, complete, notify</c>):
    /// finished (pending) or a work in progress, and the description. Answers the set's thread id.
    /// </summary>
    internal static async Task<IResult> StablePostAsync(HttpContext context, BeatmapSubmissions submissions, CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        if (await StableUserAsync(context, form["u"], form["p"], cancellationToken).ConfigureAwait(false) is not { } user)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!int.TryParse(form["b"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var setId))
            return Results.NotFound();

        // The editor puts a generated header above the mapper's own text.
        var message = form["message"].ToString();
        var separator = message.LastIndexOf("---------------\n", StringComparison.Ordinal);
        var description = separator >= 0 ? message[(separator + "---------------\n".Length)..] : message;
        try
        {
            var thread = await submissions.DescribeAsync(user, setId, form["complete"] == "1", description, cancellationToken).ConfigureAwait(false);
            return Results.Text(thread.ToString(CultureInfo.InvariantCulture), "text/plain");
        }
        catch (SubmissionException ex)
        {
            return Results.StatusCode(ex.Status);
        }
    }

    private static async Task<User?> StableUserAsync(HttpContext context, string? username, string? passwordMd5, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(passwordMd5))
            return null;
        var users = context.RequestServices.GetRequiredService<IUserRepository>();
        var passwords = context.RequestServices.GetRequiredService<IPasswordHasher>();
        return await users.GetByUsernameAsync(username, cancellationToken).ConfigureAwait(false) is { } user
               && passwords.Verify(passwordMd5, user.PasswordBcrypt)
            ? user
            : null;
    }

    private static IResult StableError(SubmissionException ex) => ex.Status switch
    {
        403 when ex.Message.Contains("ranked", StringComparison.Ordinal) => StableError(3, "The beatmap is already ranked. You cannot update ranked maps."),
        403 => StableError(1, ex.Message),
        404 => StableError(2, ex.Message),
        _ => StableError(5, ex.Message),
    };

    private static IResult StableError(int code, string message) =>
        Results.Text(string.Create(CultureInfo.InvariantCulture, $"{code}\n{message}"), "text/plain");

    // ── osu!(lazer) ────────────────────────────────────────────────────────────────────────

    /// <summary><c>PUT /beatmapsets</c>: create a set or change its difficulties, handing out ids.</summary>
    internal static async Task<IResult> LazerPutAsync(HttpContext context, LazerPutBeatmapSet body, BeatmapSubmissions submissions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await LazerUserAsync(context, cancellationToken).ConfigureAwait(false) is not { } user)
            return Results.Unauthorized();

        int? target = body.Target switch
        {
            "WIP" => CustomBeatmapSet.Wip,
            "Pending" => CustomBeatmapSet.Pending,
            _ => -99,
        };
        var keep = body.BeatmapsToKeep ?? [];
        try
        {
            if (body.BeatmapsetId is { } requested && requested < Beatmap.CustomIdFloor)
                throw new SubmissionException(403, "That beatmap set belongs to osu!, not this server.");
            if (body.BeatmapsetId is null && keep.Count > 0)
                throw new SubmissionException(422, "Cannot specify beatmaps to keep when creating a new beatmap set.");
            if (body.BeatmapsToCreate < 0)
                throw new SubmissionException(422, "The beatmap set must contain at least one beatmap.");

            var wanted = keep.Select(id => (int?)id).Concat(Enumerable.Repeat<int?>(null, Math.Min(body.BeatmapsToCreate, BeatmapSubmissions.MaxDifficulties + 1))).ToList();
            var ids = await submissions.ReserveAsync(user, body.BeatmapsetId, wanted, target, strictIds: true, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                beatmapset_id = ids.SetId,
                beatmap_ids = ids.BeatmapIds,
                files = submissions.CurrentFiles(ids.SetId).Select(f => new { filename = f.Name, sha2_hash = f.Sha256 }),
            });
        }
        catch (SubmissionException ex)
        {
            return LazerError(ex);
        }
    }

    /// <summary><c>PUT /beatmapsets/{id}</c>: the whole package (form file <c>beatmapArchive</c>).</summary>
    internal static async Task<IResult> LazerReplaceAsync(int setId, HttpContext context, BeatmapSubmissions submissions, CancellationToken cancellationToken)
    {
        if (await LazerUserAsync(context, cancellationToken).ConfigureAwait(false) is not { } user)
            return Results.Unauthorized();
        if (!context.Request.HasFormContentType)
            return LazerError(new SubmissionException(422, "The beatmap package is missing."));
        var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        if (form.Files["beatmapArchive"] is not { Length: > 0 } file)
            return LazerError(new SubmissionException(422, "The beatmap package is missing."));

        try
        {
            await submissions.UploadAsync(user, setId, await ReadAllAsync(file, cancellationToken).ConfigureAwait(false), "osu!(lazer)", cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (SubmissionException ex)
        {
            return LazerError(ex);
        }
    }

    /// <summary><c>PATCH /beatmapsets/{id}</c>: changed files (<c>filesChanged</c>) and removed ones (<c>filesDeleted</c>).</summary>
    internal static async Task<IResult> LazerPatchAsync(int setId, HttpContext context, BeatmapSubmissions submissions, CancellationToken cancellationToken)
    {
        if (await LazerUserAsync(context, cancellationToken).ConfigureAwait(false) is not { } user)
            return Results.Unauthorized();
        var form = context.Request.HasFormContentType ? await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false) : null;

        var changed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in form?.Files.GetFiles("filesChanged") ?? [])
            changed[file.FileName] = await ReadAllAsync(file, cancellationToken).ConfigureAwait(false);
        var deleted = form?["filesDeleted"].OfType<string>().ToList() ?? [];

        try
        {
            await submissions.PatchAsync(user, setId, changed, deleted, cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (SubmissionException ex)
        {
            return LazerError(ex);
        }
    }

    private static async Task<User?> LazerUserAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var tokens = context.RequestServices.GetRequiredService<LazerTokens>();
        if (await tokens.UserIdAsync(context.Request, cancellationToken).ConfigureAwait(false) is not { } userId)
            return null;
        return await context.RequestServices.GetRequiredService<IUserRepository>().GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lazer shows the <c>error</c> of a 422; 403 and 404 carry it too.</summary>
    private static IResult LazerError(SubmissionException ex) =>
        Results.Json(new { error = ex.Message }, statusCode: ex.Status);

    private static async Task<byte[]> ReadAllAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        using var copy = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
        await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.ToArray();
    }

#pragma warning disable CA5351 // the stable client checks uploads with MD5
    private static string Md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));
#pragma warning restore CA5351
}
