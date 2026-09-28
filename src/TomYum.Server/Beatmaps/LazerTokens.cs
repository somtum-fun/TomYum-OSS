using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// Who sent a request with a lazer login (an osu-web OAuth access token, <c>Authorization: Bearer</c>):
/// the token must be signed by the website's Passport key (<c>OsuWeb:OAuthPublicKeyPath</c>, default
/// <c>oauth-public.key</c>), be for lazer's client (<c>OsuWeb:LazerClientId</c>, default 5), and
/// still be valid in <c>oauth_access_tokens</c>. The same checks as ppy's spectator and submission services.
/// </summary>
public sealed class LazerTokens
{
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LazerTokens> _logger;
    private readonly JsonWebTokenHandler _handler = new();
    private SecurityKey? _key;

    public LazerTokens(IConfiguration configuration, IServiceScopeFactory scopes, ILogger<LazerTokens> logger)
    {
        _configuration = configuration;
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>The user the request's token belongs to, or null when it has no valid one.</summary>
    public async Task<int?> UserIdAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || Key() is not { } key)
            return null;

        var result = await _handler.ValidateTokenAsync(header["Bearer ".Length..].Trim(), new TokenValidationParameters
        {
            IssuerSigningKey = key,
            ValidateIssuer = false,
            ValidAudience = _configuration["OsuWeb:LazerClientId"] ?? "5",
            ValidateLifetime = true,
        }).ConfigureAwait(false);
        if (!result.IsValid || result.SecurityToken is not JsonWebToken token || string.IsNullOrEmpty(token.Id))
            return null;

        // Signed isn't enough: the user may have signed out (revoked) since.
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OsuWebDbContext>();
        var users = await db.Database
            .SqlQuery<long?>($"SELECT user_id AS Value FROM oauth_access_tokens WHERE id = {token.Id} AND revoked = 0 AND (expires_at IS NULL OR expires_at > UTC_TIMESTAMP())")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return users.Count == 1 && users[0] is { } userId ? (int)userId : null;
    }

    private SecurityKey? Key()
    {
        if (_key is not null)
            return _key;
        var path = _configuration["OsuWeb:OAuthPublicKeyPath"] ?? "oauth-public.key";
        if (!File.Exists(path))
        {
            _logger.LogWarning("Lazer logins can't be checked: the website's OAuth public key isn't at {Path}.", Path.GetFullPath(path));
            return null;
        }
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(path));
        _key = new RsaSecurityKey(rsa);
        return _key;
    }
}
