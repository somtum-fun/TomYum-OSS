namespace TomYum.Bancho;

/// <summary>
/// The negative <c>USER_ID</c> (id 5) sentinels the client branches on when login does
/// not succeed. A non-negative value in the USER_ID packet is the real user id; these
/// negative codes signal the various failure modes. See
/// <c>memories/protocols/bancho-stable.md</c> §3.
/// </summary>
public enum LoginFailureReason
{
    /// <summary>Authentication failed: unknown username or wrong password.</summary>
    AuthenticationFailed = -1,

    /// <summary>The client is too old to connect.</summary>
    ClientTooOld = -2,

    /// <summary>The account is banned.</summary>
    Banned = -3,

    /// <summary>A generic server error occurred.</summary>
    ServerError = -5,

    /// <summary>The account needs supporter to connect (e.g. a supporter-only server).</summary>
    SupporterRequired = -6,

    /// <summary>The account password must be reset before connecting.</summary>
    PasswordReset = -7,

    /// <summary>The account requires verification.</summary>
    VerificationRequired = -8,
}
