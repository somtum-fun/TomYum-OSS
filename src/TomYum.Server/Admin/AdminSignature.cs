using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TomYum.Server.Admin;

/// <summary>
/// Signs and checks admin API requests (osu-web's staff panels → TomYum). The signature is a hex
/// HMAC-SHA256, keyed with the shared InterOp secret, over the method, the path + query (which
/// carries <c>timestamp=</c>), the acting staff member's id and the raw body — so a captured
/// request can't be replayed later or with a different body or actor.
/// </summary>
public static class AdminSignature
{
    /// <summary>Header carrying the signature.</summary>
    public const string SignatureHeader = "X-TomYum-Signature";

    /// <summary>Header carrying the osu-web user id of the staff member acting (audit only).</summary>
    public const string ActorHeader = "X-Actor-Id";

    /// <summary>How far a request's <c>timestamp</c> may be from now.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>The hex signature for a request.</summary>
    public static string Compute(string secret, string method, string pathAndQuery, string actorId, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        ArgumentNullException.ThrowIfNull(actorId);

        var head = Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture, $"{method.ToUpperInvariant()}\n{pathAndQuery}\n{actorId}\n"));
        var message = new byte[head.Length + body.Length];
        head.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(head.Length));

        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
    }

    /// <summary>Whether <paramref name="signature"/> is valid for the request (constant-time compare).</summary>
    public static bool IsValid(string secret, string method, string pathAndQuery, string actorId, ReadOnlySpan<byte> body, string? signature)
    {
        if (string.IsNullOrEmpty(signature))
            return false;

        var expected = Compute(secret, method, pathAndQuery, actorId, body);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.ToLowerInvariant()));
    }

    /// <summary>Whether a request's unix <paramref name="timestamp"/> is within <see cref="MaxClockSkew"/> of <paramref name="now"/>.</summary>
    public static bool IsFresh(long timestamp, DateTimeOffset now) =>
        Math.Abs(now.ToUnixTimeSeconds() - timestamp) <= MaxClockSkew.TotalSeconds;
}
