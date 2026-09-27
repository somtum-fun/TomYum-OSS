namespace TomYum.Server.Beatmaps;

/// <summary>
/// osu! v1 API settings, bound from configuration (<c>Osu</c> section / <c>OSU_API_KEY</c> env).
/// With no key set, <see cref="BaseUrl"/> defaults to the keyless <c>osu.direct</c> mirror so a
/// fresh deployment still resolves beatmap metadata (mirrors bancho.py's fallback).
/// </summary>
public sealed class OsuApiOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Osu";

    /// <summary>The official keyed endpoint (requires <see cref="ApiKey"/>).</summary>
    public const string OfficialBaseUrl = "https://old.ppy.sh/api/";

    /// <summary>The keyless community mirror used when no API key is configured.</summary>
    public const string MirrorBaseUrl = "https://osu.direct/api/";

    /// <summary>osu! v1 API key. When empty, the keyless mirror is used.</summary>
    public string? ApiKey { get; set; }

    /// <summary>osu! API v2 OAuth application id (descriptions, nominators); optional.</summary>
    public string? ClientId { get; set; }

    /// <summary>osu! API v2 OAuth application secret; optional.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Effective API base address: the official endpoint when an <see cref="ApiKey"/> is present,
    /// otherwise the keyless mirror. Always ends in <c>/</c> so relative requests compose cleanly.
    /// </summary>
    public string BaseUrl => string.IsNullOrEmpty(ApiKey) ? MirrorBaseUrl : OfficialBaseUrl;
}
