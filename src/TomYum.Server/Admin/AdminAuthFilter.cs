using System.Globalization;
using Microsoft.AspNetCore.Http.Extensions;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Admin;

/// <summary>
/// Lets an admin API request through only when it carries a fresh, valid
/// <see cref="AdminSignature"/>. osu-web checks the staff member's permissions before signing;
/// TomYum trusts any correctly signed request. Without a shared secret the API doesn't exist (404).
/// </summary>
public sealed class AdminAuthFilter : IEndpointFilter
{
    /// <summary><c>HttpContext.Items</c> key holding the acting staff member's osu-web user id.</summary>
    public const string ActorItemKey = "TomYum.Admin.ActorId";

    private readonly OsuWebInteropOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AdminAuthFilter> _logger;

    public AdminAuthFilter(OsuWebInteropOptions options, TimeProvider time, ILogger<AdminAuthFilter> logger)
    {
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        var request = http.Request;
        if (string.IsNullOrWhiteSpace(_options.InteropSecret))
            return Results.NotFound();

        if (!long.TryParse(request.Query["timestamp"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp)
            || !AdminSignature.IsFresh(timestamp, _time.GetUtcNow()))
        {
            return Results.Unauthorized();
        }

        // The body may already have been read by model binding; it's buffered (UseAdminRequestBuffering),
        // so rewind, read it for the signature, and rewind again.
        if (!request.Body.CanSeek)
            request.EnableBuffering();
        request.Body.Position = 0;
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, http.RequestAborted).ConfigureAwait(false);
        request.Body.Position = 0;

        var actor = request.Headers[AdminSignature.ActorHeader].ToString();
        if (!AdminSignature.IsValid(_options.InteropSecret, request.Method, request.GetEncodedPathAndQuery(), actor,
                buffer.GetBuffer().AsSpan(0, (int)buffer.Length), request.Headers[AdminSignature.SignatureHeader]))
        {
            _logger.LogWarning("Rejected admin API request {Method} {Path}: bad signature.", request.Method, request.Path);
            return Results.Unauthorized();
        }

        http.Items[ActorItemKey] = actor;
        _logger.LogInformation("Admin API {Method} {Path} by osu-web user {Actor}.", request.Method, request.Path, actor);
        return await next(context).ConfigureAwait(false);
    }
}
