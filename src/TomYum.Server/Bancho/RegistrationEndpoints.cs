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

        var ip = LoginIps.ClientAddress(context);
        var header = context.Request.Headers["CF-IPCountry"].ToString();
        var countryIso = IpCountry.FromHeader(header)
                         ?? context.RequestServices.GetService<IpCountry>()?.Lookup(ip)
                         ?? "xx";
        var country = countryIso.ToLowerInvariant();

        // bancho.py's field rules first, then the website's sign-up rules (IP bans, one account per
        // network a day, no VPN, proxy or hosting network; osu-web's TomYumRegistrationGuard, the same
        // rules lazer's sign-up goes through), then the account.
        var result = await registration
            .RegisterAsync(username, email, password, create: false, country, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsValid)
            return FormErrors(result.Errors);

        var blocked = await osuWebSync
            .CheckRegistrationAsync(ip, username, countryIso, quick: !create, cancellationToken)
            .ConfigureAwait(false);
        if (blocked is not null)
        {
            // Not about one field: on the last one, as bancho.py put "registration disabled".
            return FormErrors(new Dictionary<string, string> { ["password"] = blocked });
        }

        if (create)
        {
            result = await registration
                .RegisterAsync(username, email, password, create: true, country, cancellationToken, ip)
                .ConfigureAwait(false);
            if (!result.IsValid)
                return FormErrors(result.Errors);
        }

        // Website search and the sign-up rules' count (osu-web mode). Best-effort: the account exists either way.
        if (result.Created is { } created)
        {
            try
            {
                await osuWebSync.RegistrationCreatedAsync(created.Id, created.Username, ip, countryIso, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Telling osu-web's sign-up rules about new account {UserId} failed", created.Id);
            }

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

    /// <summary>bancho.py's shape: <c>{"form_error": {"user": {"&lt;field&gt;": ["&lt;joined messages&gt;"]}}}</c>, HTTP 400.</summary>
    private static IResult FormErrors(IReadOnlyDictionary<string, string> errors)
    {
        var fields = errors.ToDictionary(kvp => kvp.Key, kvp => new[] { kvp.Value });
        return Results.Json(new { form_error = new { user = fields } }, statusCode: 400);
    }
}
