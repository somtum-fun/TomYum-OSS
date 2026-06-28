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
    /// <summary>Registers the <c>POST /users</c> in-game registration handler.</summary>
    public static void MapRegistration(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/users", HandleRegisterAsync);
    }

    private static async Task<IResult> HandleRegisterAsync(
        HttpContext context,
        RegistrationService registration,
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

        // TODO (geoloc slice): resolve the client country from CF-IPCountry / X-Forwarded-For.
        // For now new accounts default to "xx" until IP geolocation lands.
        var result = await registration
            .RegisterAsync(username, email, password, create, country: "xx", cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsValid)
        {
            // bancho.py shape: {"form_error": {"user": {"<field>": ["<joined messages>"]}}}.
            var fields = result.Errors.ToDictionary(kvp => kvp.Key, kvp => new[] { kvp.Value });
            return Results.Json(
                new { form_error = new { user = fields } },
                statusCode: 400);
        }

        // The client only proceeds when the body is exactly "ok" (both validate and create).
        return Results.Text("ok", "text/plain");
    }
}
