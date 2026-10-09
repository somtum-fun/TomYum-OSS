using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TomYum.Server.Anticheat;

/// <summary>
/// Checks for old (2008-2015) builds patched for this server (config section <c>Anticheat:OldClients</c>,
/// docs/old-clients.md). Each patched build carries its own key, <c>HMAC-SHA256(Secret, build)</c>, and
/// signs two things with it: its login (a <c>tomyum-ac:&lt;build&gt;:&lt;flags&gt;:&lt;mac&gt;</c> capability on the
/// login line) and each score it submits (the <c>tac</c> field, same shape). The flags are what the client
/// found when it checked itself (<see cref="OldClientFindings"/>). With a <see cref="Secret"/> set the proofs are
/// checked and what fails is logged; with <see cref="Require"/> on too, an old build without a valid proof, or with
/// a flag raised, can't log in or submit.
/// </summary>
public sealed class OldClientProofOptions
{
    public const string Section = "Anticheat:OldClients";

    /// <summary>Whether old builds that fail the check are refused (off: it's only logged). Default: off.</summary>
    public bool Require { get; set; }

    /// <summary>The secret the patched builds' keys are made from (the patcher gets the same one). Empty: off.</summary>
    public string? Secret { get; set; }

    /// <summary>Whether the proofs are checked at all: a <see cref="Secret"/> is set.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(Secret);
}

/// <summary>What a patched old build found when it checked itself (bits of the proof's flags).</summary>
[Flags]
public enum OldClientFindings
{
    None = 0,

    /// <summary>A debugger is attached to the game.</summary>
    Debugger = 1,

    /// <summary>A known cheat tool is running.</summary>
    CheatProcess = 2,
}

/// <summary>A proof sent by a patched old build: which build, what it found, and its signature.</summary>
/// <param name="Build">The build's name as the patcher knows it (e.g. <c>b20140127</c>).</param>
/// <param name="Flags">What the build found when it checked itself.</param>
/// <param name="Mac">The hex HMAC over the login or the score.</param>
public sealed record OldClientProof(string Build, OldClientFindings Flags, string Mac)
{
    /// <summary>The login line's capability that carries the proof.</summary>
    public const string Capability = "tomyum-ac";

    /// <summary>The score submission's field that carries the proof.</summary>
    public const string ScoreField = "tac";

    /// <summary>Reads <c>&lt;build&gt;:&lt;flags&gt;:&lt;mac&gt;</c> (with or without the <c>tomyum-ac:</c> prefix); null when it isn't one.</summary>
    /// <param name="text">The capability or field value.</param>
    public static OldClientProof? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var value = text.Trim();
        if (value.StartsWith(Capability + ":", StringComparison.OrdinalIgnoreCase))
            value = value[(Capability.Length + 1)..];
        var parts = value.Split(':');
        if (parts.Length != 3 || parts[0].Length is 0 or > 64 || parts[2].Length != 64
            || !int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags))
            return null;
        return new OldClientProof(parts[0], (OldClientFindings)flags, parts[2].ToLowerInvariant());
    }

    /// <summary>A build's key: <c>HMAC-SHA256(secret, build)</c>, as the patcher bakes it in.</summary>
    /// <param name="secret">The shared secret (<see cref="OldClientProofOptions.Secret"/>).</param>
    /// <param name="build">The build's name.</param>
    public static byte[] BuildKey(string secret, string build) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(build));

    /// <summary>What a login's proof signs.</summary>
    /// <param name="username">The name as typed.</param>
    /// <param name="passwordMd5">The password's md5 as sent.</param>
    /// <param name="flags">The proof's flags.</param>
    public static string LoginMessage(string username, string passwordMd5, OldClientFindings flags) =>
        string.Create(CultureInfo.InvariantCulture, $"login|{username}|{passwordMd5}|{(int)flags:x}");

    /// <summary>What a score's proof signs: the score's text before it's encrypted.</summary>
    /// <param name="score">The score's fields, as the client wrote them.</param>
    /// <param name="flags">The proof's flags.</param>
    public static string ScoreMessage(string score, OldClientFindings flags) =>
        string.Create(CultureInfo.InvariantCulture, $"score|{score}|{(int)flags:x}");

    /// <summary>Whether <see cref="Mac"/> signs <paramref name="message"/> with this build's key.</summary>
    /// <param name="secret">The shared secret.</param>
    /// <param name="message">What was signed (<see cref="LoginMessage"/> or <see cref="ScoreMessage"/>).</param>
    public bool Signs(string secret, string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(message);
        var expected = HMACSHA256.HashData(BuildKey(secret, Build), Encoding.UTF8.GetBytes(message));
        return Mac.Length == 64
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Convert.ToHexStringLower(expected)), Encoding.ASCII.GetBytes(Mac));
    }

    /// <summary>The raised flags in words, for the staff log.</summary>
    public string FlagText() => Flags == OldClientFindings.None ? "none" : Flags.ToString();
}
