using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// Permission to upload one <c>.osz</c>, handed out by the website (the BN panel checks who may
/// upload and signs this with the shared InterOp secret): the browser then sends the file straight
/// here, so the website's own upload limits don't apply. A hex HMAC-SHA256 over
/// <c>upload\n{user}\n{set}\n{expires}</c>, where set is empty for a new set.
/// </summary>
public static class UploadTicket
{
    public static string Compute(string secret, int userId, int? setId, long expires)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var message = string.Create(CultureInfo.InvariantCulture, $"upload\n{userId}\n{setId}\n{expires}");
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message)));
    }

    public static bool IsValid(string secret, int userId, int? setId, long expires, string? signature, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(signature) || expires < now.ToUnixTimeSeconds())
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Compute(secret, userId, setId, expires)), Encoding.ASCII.GetBytes(signature.ToLowerInvariant()));
    }
}

/// <summary>
/// <c>POST /web/tomyum/beatmapset-upload?user=&amp;set=&amp;expires=&amp;signature=</c> with the
/// <c>.osz</c> as form field <c>osz</c>: stores it as a custom set (<see cref="CustomBeatmapImporter"/>)
/// and answers JSON for the BN panel.
/// </summary>
public static class CustomUploadEndpoints
{
    public const string Path = "/web/tomyum/beatmapset-upload";

    public static void MapCustomUploads(this IEndpointRouteBuilder app, params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost(Path, UploadAsync)
            .RequireHost(hosts)
            .WithMetadata(new RequestSizeLimitAttribute(CustomBeatmapImporter.MaxOszBytes + 1024 * 1024));
    }

    internal static async Task<IResult> UploadAsync(HttpContext context, CustomBeatmapImporter importer, TimeProvider time, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var secret = context.RequestServices.GetService<OsuWebInteropOptions>()?.InteropSecret;
        if (string.IsNullOrWhiteSpace(secret))
            return Results.NotFound();

        int? setId = int.TryParse(request.Query["set"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var set) ? set : null;
        if (!int.TryParse(request.Query["user"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
            || !long.TryParse(request.Query["expires"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expires)
            || !UploadTicket.IsValid(secret, userId, setId, expires, request.Query["signature"], time.GetUtcNow()))
        {
            return Error("This upload link has expired; reload the page and try again.", StatusCodes.Status403Forbidden);
        }

        if (!request.HasFormContentType)
            return Error("Choose an .osz file to upload.");
        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        if (form.Files["osz"] is not { Length: > 0 } file)
            return Error("Choose an .osz file to upload.");
        if (file.Length > CustomBeatmapImporter.MaxOszBytes)
            return Error(string.Create(CultureInfo.InvariantCulture, $"The file is too big (the limit is {CustomBeatmapImporter.MaxOszBytes / 1024 / 1024} MB)."));

        byte[] bytes;
        await using (var stream = file.OpenReadStream())
        {
            using var copy = new MemoryStream((int)file.Length);
            await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            bytes = copy.ToArray();
        }

        try
        {
            var result = await importer.ImportAsync(bytes, userId, setId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                set_id = result.SetId,
                result.Artist,
                result.Title,
                result.Creator,
                owner_id = result.OwnerId,
                result.Updated,
                difficulties = result.Difficulties.Select(d => new { beatmap_id = d.BeatmapId, d.Version, d.Mode, star_rating = d.StarRating }),
            });
        }
        catch (CustomUploadException ex)
        {
            return Error(ex.Message);
        }
    }

    private static IResult Error(string message, int status = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: status);
}
