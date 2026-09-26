using System.Globalization;

namespace TomYum.Bancho;

/// <summary>
/// The parsed credentials and client metadata from an osu!stable login. The first
/// bancho request carries no <c>osu-token</c> header and its body is plain UTF-8 text
/// (not binary), laid out as described in <c>memories/protocols/bancho-stable.md</c> §3:
/// <code>
/// &lt;username&gt;\n
/// &lt;password_md5&gt;\n
/// &lt;osu_version&gt;|&lt;utc_offset&gt;|&lt;display_city&gt;|&lt;client_hashes&gt;|&lt;pm_private&gt;\n
/// </code>
/// </summary>
/// <param name="Username">Login name as typed in the client (may contain spaces).</param>
/// <param name="PasswordMd5">Hex MD5 of the password as sent by the client.</param>
/// <param name="OsuVersion">Client version string, e.g. <c>b20231228.1</c>.</param>
/// <param name="UtcOffset">Client UTC offset in hours.</param>
/// <param name="DisplayCity">Whether the client opted to display its city (the <c>"1"</c>/<c>"0"</c> flag).</param>
/// <param name="ClientHashes">The raw client hash bundle (osu path / adapters / uninstall / disk signature).</param>
/// <param name="PmPrivate">Whether the client blocks PMs from non-friends at login.</param>
public sealed record LoginRequest(
    string Username,
    string PasswordMd5,
    string OsuVersion,
    int UtcOffset,
    bool DisplayCity,
    ClientHashes ClientHashes,
    bool PmPrivate)
{
    /// <summary>
    /// Parses a raw login body. Returns <see langword="false"/> (with <paramref name="request"/>
    /// set to <see langword="null"/>) when the body is malformed, so the caller can reply with
    /// the <c>USER_ID = -1</c> failure sentinel rather than throwing on hostile input.
    /// </summary>
    /// <param name="body">The decoded UTF-8 login body.</param>
    /// <param name="request">The parsed request when this returns <see langword="true"/>.</param>
    public static bool TryParse(string body, out LoginRequest? request)
    {
        request = null;
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        // Mirror bancho.py parse_login_data: split into exactly 3 lines (the 3rd line may
        // itself contain no further newlines). A trailing newline yields an empty 4th part.
        var lines = body.Split('\n');
        if (lines.Length < 3)
        {
            return false;
        }

        var username = lines[0];
        var passwordMd5 = lines[1];
        if (username.Length == 0 || passwordMd5.Length == 0)
        {
            return false;
        }

        // osu_version|utc_offset|display_city|client_hashes|pm_private
        var info = lines[2].Split('|');
        if (info.Length < 5)
        {
            return false;
        }

        if (!int.TryParse(info[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var utcOffset))
        {
            return false;
        }

        if (!ClientHashes.TryParse(info[3], out var hashes))
        {
            return false;
        }

        request = new LoginRequest(
            Username: username,
            PasswordMd5: passwordMd5,
            OsuVersion: info[0],
            UtcOffset: utcOffset,
            DisplayCity: info[2] == "1",
            ClientHashes: hashes!,
            PmPrivate: info[4].StartsWith('1'));
        return true;
    }
}

/// <summary>
/// The client hardware/identity hashes sent at login (used for multi-account and
/// hardware-ban detection). Format (a trailing <c>:</c> is stripped before splitting):
/// <c>&lt;osu_path_md5&gt;:&lt;adapters_str&gt;:&lt;adapters_md5&gt;:&lt;uninstall_md5&gt;:&lt;disk_signature_md5&gt;</c>.
/// </summary>
/// <param name="OsuPathMd5">MD5 of the client install path.</param>
/// <param name="AdaptersStr">Raw network adapter list (or <c>runningunderwine</c>).</param>
/// <param name="AdaptersMd5">MD5 of the adapter list.</param>
/// <param name="UninstallMd5">MD5 of the uninstall id.</param>
/// <param name="DiskSignatureMd5">MD5 of the disk signature.</param>
public sealed record ClientHashes(
    string OsuPathMd5,
    string AdaptersStr,
    string AdaptersMd5,
    string UninstallMd5,
    string DiskSignatureMd5)
{
    /// <summary>Parses the colon-delimited client hash bundle. The trailing <c>:</c> is tolerated.</summary>
    public static bool TryParse(string raw, out ClientHashes? hashes)
    {
        hashes = null;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        // Client appends a trailing ':' after the disk signature; drop it before splitting.
        var trimmed = raw.EndsWith(':') ? raw[..^1] : raw;
        var parts = trimmed.Split(':');
        if (parts.Length < 5)
        {
            return false;
        }

        hashes = new ClientHashes(parts[0], parts[1], parts[2], parts[3], parts[4]);
        return true;
    }
}
