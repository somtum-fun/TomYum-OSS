using System.Text;
using System.Text.RegularExpressions;

using TomYum.Plugins.Abstractions;

namespace TomYum.Server.Plugins;

/// <summary>
/// Shared table of plugin-contributed HTTP routes. Populated when plugins initialize (through the
/// per-plugin <see cref="ScopedHttpRouteRegistry"/>) and read at request time by the plugin-routing
/// middleware, which runs before the core endpoints. Routes may bind to a subdomain (the Session
/// plugin's <c>session.&lt;domain&gt;</c> vhost) or, with <c>subdomain == null</c>, match on
/// <em>every</em> host, the internal <c>server</c> one included: a plugin that wants one host checks
/// it itself. The core's own paths (<see cref="ReservedPrefixes"/>) are never a plugin's: routes
/// there aren't registered, and those paths never match.
/// </summary>
public sealed class PluginRouteTable
{
    /// <summary>Paths that belong to the core (the admin API, the gateway link): no plugin route serves them.</summary>
    public static readonly IReadOnlyList<string> ReservedPrefixes = ["/_admin", "/_gateway"];

    private readonly List<PluginRoute> _routes = [];
    private readonly object _lock = new();

    /// <summary>A snapshot of all registered routes.</summary>
    public IReadOnlyList<PluginRoute> Routes
    {
        get
        {
            lock (_lock)
            {
                return _routes.ToArray();
            }
        }
    }

    internal IDisposable Add(PluginRoute route)
    {
        lock (_lock)
        {
            _routes.Add(route);
        }

        return new Registration(this, route);
    }

    private void Remove(PluginRoute route)
    {
        lock (_lock)
        {
            _routes.Remove(route);
        }
    }

    /// <summary>
    /// Finds the first route matching <paramref name="method"/> + <paramref name="path"/> and, when a
    /// route is subdomain-bound, the request's first host label <paramref name="hostLabel"/>.
    /// </summary>
    public PluginRouteMatch? Match(string method, string path, string? hostLabel)
    {
        if (IsReserved(path))
            return null;

        foreach (var route in Routes)
        {
            if (!string.Equals(route.Method, method, StringComparison.OrdinalIgnoreCase))
                continue;

            if (route.Subdomain is not null
                && !string.Equals(route.Subdomain, hostLabel, StringComparison.OrdinalIgnoreCase))
                continue;

            var match = route.Pattern.Match(path);
            if (!match.Success)
                continue;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in route.ParamNames)
                values[name] = match.Groups[name].Value;

            return new PluginRouteMatch(route, values);
        }

        return null;
    }

    /// <summary>Whether <paramref name="path"/> (a request path or a route template) is under one of the <see cref="ReservedPrefixes"/>.</summary>
    internal static bool IsReserved(string path)
    {
        var value = new PathString(path.StartsWith('/') ? path : "/" + path);
        return ReservedPrefixes.Any(prefix => value.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Compiles a route template like <c>/api/v1/clans/{id}</c> into a regex + captured names.</summary>
    internal static (Regex Pattern, IReadOnlyList<string> ParamNames) Compile(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        var names = new List<string>();
        var builder = new StringBuilder("^");
        var segments = template.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0)
                builder.Append('/');

            var segment = segments[i];
            if (segment.Length >= 2 && segment[0] == '{' && segment[^1] == '}')
            {
                var name = segment[1..^1];
                names.Add(name);
                builder.Append("(?<").Append(name).Append(">[^/]+)");
            }
            else
            {
                builder.Append(Regex.Escape(segment));
            }
        }

        builder.Append('$');
        return (new Regex(builder.ToString(), RegexOptions.CultureInvariant), names);
    }

    private sealed class Registration : IDisposable
    {
        private readonly PluginRoute _route;
        private PluginRouteTable? _owner;

        public Registration(PluginRouteTable owner, PluginRoute route)
        {
            _owner = owner;
            _route = route;
        }

        public void Dispose()
        {
            _owner?.Remove(_route);
            _owner = null;
        }
    }
}

/// <summary>A compiled plugin HTTP route.</summary>
public sealed record PluginRoute(
    string PluginId,
    string Method,
    string Template,
    Regex Pattern,
    IReadOnlyList<string> ParamNames,
    bool RequireAuth,
    string? Subdomain,
    Func<PluginHttpRequest, CancellationToken, Task<PluginHttpResponse>> Handler);

/// <summary>A route match plus the values captured from the template.</summary>
public sealed record PluginRouteMatch(PluginRoute Route, IReadOnlyDictionary<string, string> RouteValues);

/// <summary>
/// Per-plugin facade over <see cref="PluginRouteTable"/> that tags routes with the owning plugin id
/// and compiles their templates. Handed to a plugin as <see cref="IPluginContext.Http"/>.
/// </summary>
internal sealed class ScopedHttpRouteRegistry : IHttpRouteRegistry
{
    private readonly PluginRouteTable _table;
    private readonly string _pluginId;
    private readonly ILogger? _logger;

    public ScopedHttpRouteRegistry(PluginRouteTable table, string pluginId, ILogger? logger = null)
    {
        _table = table;
        _pluginId = pluginId;
        _logger = logger;
    }

    public IDisposable Map(
        string method,
        string pattern,
        Func<PluginHttpRequest, CancellationToken, Task<PluginHttpResponse>> handler,
        bool requireAuth = false,
        string? subdomain = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        // The admin API and the gateway are the core's; a plugin route there would run before them.
        if (PluginRouteTable.IsReserved(pattern))
        {
            _logger?.LogWarning("Plugin {PluginId} tried to map {Method} {Pattern}, a path that belongs to the server; it isn't mapped.", _pluginId, method, pattern);
            return new NotMapped();
        }

        var (regex, names) = PluginRouteTable.Compile(pattern);
        return _table.Add(new PluginRoute(_pluginId, method, pattern, regex, names, requireAuth, subdomain, handler));
    }

    private sealed class NotMapped : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
