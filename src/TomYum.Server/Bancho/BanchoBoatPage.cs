using System.Reflection;

namespace TomYum.Server.Bancho;

/// <summary>
/// The browser-facing status page served at <c>GET /</c> on the bancho subdomains — an animated
/// homage to the osu!bancho boat. The HTML lives in the embedded <c>Bancho/BanchoBoat.html</c>
/// resource (pure CSS, self-contained); <see cref="Render"/> injects the running version.
/// </summary>
public static class BanchoBoatPage
{
    private const string VersionPlaceholder = "{{VERSION}}";

    private static readonly string Template = LoadTemplate();

    /// <summary>The full HTML page with the current version substituted in.</summary>
    public static string Render(string version) =>
        Template.Replace(VersionPlaceholder, version, StringComparison.Ordinal);

    private static string LoadTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("BanchoBoat.html", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded resource 'BanchoBoat.html' was not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
