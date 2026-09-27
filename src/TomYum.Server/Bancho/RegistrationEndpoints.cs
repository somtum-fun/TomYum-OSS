using TomYum.Server.OsuWeb;

namespace TomYum.Server.Bancho;

/// <summary>
/// Maps the osu!stable in-game registration endpoint (<c>POST /users</c>), served on the
/// <c>osu.</c> host. The client posts a multipart form with <c>user[username]</c>,
/// <c>user[user_email]</c>, <c>user[password]</c>, and a <c>check</c> flag; it validates on
/// every keystroke (<c>check != 0</c>) and creates on final submit (<c>check == 0</c>).
/// See <c>memories/protocols/bancho-stable.md</c> §3.
/// </summary>
public static class RegistrationEndpoints
{
    /// <summary>
    /// Registers the <c>POST /users</c> in-game registration handler, constrained to the
    /// <c>osu.</c> host (plus localhost for development). The host patterns come from
    /// <see cref="BanchoHosts.Web"/>.
    /// </summary>
    public static void MapRegistration(this IEndpointRouteBuilder app, params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/users", HandleRegisterAsync)
            .RequireHost(hosts);
    }

    private static async Task<IResult> HandleRegisterAsync(
        HttpContext context,
        RegistrationService registration,
        IOsuWebSync osuWebSync,
        ILogger<RegistrationService> logger,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            return Results.Text("Missing required params", "text/plain", statusCode: 400);
        }

        var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var username = form["user[username]"].ToString();
        var email = form["user[user_email]"].ToString();
        var password = form["user[password]"].ToString();
        var checkRaw = form["check"].ToString();

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(email) ||
            string.IsNullOrEmpty(password) || string.IsNullOrEmpty(checkRaw))
        {
            return Results.Text("Missing required params", "text/plain", statusCode: 400);
        }

        // check == 0 means "create the account"; any non-zero value is validate-only.
        var create = int.TryParse(checkRaw, out var check) && check == 0;

        var countryIso = context.Request.Headers["CF-IPCountry"].ToString();
        if (string.IsNullOrWhiteSpace(countryIso))
            countryIso = "xx";

        var result = await registration
            .RegisterAsync(username, email, password, create, country: countryIso.ToLowerInvariant(), cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsValid)
        {
            // bancho.py shape: {"form_error": {"user": {"<field>": ["<joined messages>"]}}}.
            var fields = result.Errors.ToDictionary(kvp => kvp.Key, kvp => new[] { kvp.Value });
            return Results.Json(
                new { form_error = new { user = fields } },
                statusCode: 400);
        }

        // Website search (osu-web mode). Best-effort: the account exists either way.
        if (result.Created is { } created)
        {
            try
            {
                await osuWebSync.AccountCreatedAsync(created.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "osu-web search update for new account {UserId} failed", created.Id);
            }
        }

        // The client only proceeds when the body is exactly "ok" (both validate and create).
        return Results.Text("ok", "text/plain");
    }
}
