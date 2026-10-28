using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace TomYum.Server.Admin;

/// <summary>
/// A custom lazer ruleset's mods, for the website (its boards' mod filters, and whether it has Relax/Autopilot boards):
/// the pinned DLL loaded on its own (osu!'s libraries are the server's), its ruleset made, and the mods a player can pick.
/// Kept by the DLL's hash, which never changes for a pinned release.
/// </summary>
public static class CustomRulesetMods
{
    /// <summary>A mod as the website lists it.</summary>
    public sealed record ModEntry(string Acronym, string Name, string Type);

    private const long MaxBytes = 32 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, IReadOnlyList<ModEntry>> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Loading = new(1, 1);

    public static void MapCustomRulesetMods(this RouteGroupBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        // ?url={the pinned DLL}&sha256={its hash}: [{acronym, name, type}] (404 when it can't be read).
        admin.MapGet("/custom-rulesets/mods", GetAsync);
    }

    internal static async Task<IResult> GetAsync(string url, string sha256, IHttpClientFactory http, ILoggerFactory logs, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(sha256))
            return Results.BadRequest(new { error = "url (https) and sha256 are needed." });
        if (Known.TryGetValue(sha256, out var known))
            return Results.Ok(known);

        await Loading.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Known.TryGetValue(sha256, out known))
                return Results.Ok(known);

            byte[] bytes;
            using (var client = http.CreateClient())
            {
                client.Timeout = TimeSpan.FromSeconds(60);
                bytes = await client.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);
            }
            if (bytes.Length > MaxBytes || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "The file isn't the pinned one." });

            var mods = Read(bytes);
            Known[sha256] = mods;
            return Results.Ok(mods);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logs.CreateLogger(nameof(CustomRulesetMods)).LogWarning(ex, "Custom ruleset mods: couldn't read {Url}.", url);
            return Results.NotFound(new { error = "The ruleset couldn't be read." });
        }
        finally
        {
            Loading.Release();
        }
    }

    /// <summary>The mods a player can pick in the ruleset in <paramref name="dll"/>, in the game's order.</summary>
    public static IReadOnlyList<ModEntry> Read(byte[] dll)
    {
        var context = new RulesetLoadContext();
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(dll, writable: false));
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).ToArray()!;
            }
            var rulesetType = types.First(t => t.IsSubclassOf(typeof(osu.Game.Rulesets.Ruleset)) && !t.IsAbstract);
            var ruleset = (osu.Game.Rulesets.Ruleset)Activator.CreateInstance(rulesetType)!;
            return ruleset.CreateAllMods()
                .Where(m => m.UserPlayable)
                .Select(m => new ModEntry(m.Acronym, m.Name, m.Type.ToString()))
                .DistinctBy(m => m.Acronym)
                .ToList();
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>A ruleset on its own, with osu!'s libraries (and everything else) the server's.</summary>
    private sealed class RulesetLoadContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) =>
            Default.Assemblies.FirstOrDefault(a => a.GetName().Name == assemblyName.Name);
    }
}
