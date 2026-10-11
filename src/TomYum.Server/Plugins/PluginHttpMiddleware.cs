using TomYum.Plugins.Abstractions;

namespace TomYum.Server.Plugins;

/// <summary>Wires the plugin HTTP route table into the request pipeline.</summary>
public static class PluginHttpMiddleware
{
    /// <summary>
    /// Adds terminal middleware that serves any request matching a plugin-registered route, and
    /// otherwise passes through to the rest of the pipeline. Add it before the bancho/web endpoints.
    /// </summary>
    public static void UsePluginRoutes(this WebApplication app, PluginRouteTable table)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(table);

        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var hostLabel = FirstLabel(request.Host.Host);
            var match = table.Match(request.Method, request.Path.Value ?? "/", hostLabel);
            if (match is null)
            {
                await next().ConfigureAwait(false);
                return;
            }

            if (match.Route.RequireAuth && ResolveUserId(context) is null)
            {
                // Session-token auth is provided by the Session plugin (Phase 4); until then an
                // auth-required plugin route is unauthorized rather than silently public.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var query = request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var body = (request.ContentLength ?? 0) > 0 ? request.Body : null;

            var pluginRequest = new PluginHttpRequest(
                request.Method,
                request.Path.Value ?? "/",
                match.RouteValues,
                query,
                headers,
                body,
                ResolveUserId(context));

            PluginHttpResponse response;
            try
            {
                response = await match.Route.Handler(pluginRequest, context.RequestAborted).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // a plugin route handler must not take down the whole request pipeline
            catch (Exception)
#pragma warning restore CA1031
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = response.ContentType;
            if (response.Headers is not null)
            {
                foreach (var (key, value) in response.Headers)
                    context.Response.Headers[key] = value;
            }

            // 204 and 304 have no body (Kestrel throws on a write, even an empty one).
            if (response.StatusCode is StatusCodes.Status204NoContent or StatusCodes.Status304NotModified)
                return;
            await context.Response.Body.WriteAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
        });
    }

    /// <summary>The authenticated user id for this request, or null. Set by the Session plugin later.</summary>
    private static long? ResolveUserId(HttpContext context) =>
        context.Items.TryGetValue("tomyum.plugin.userid", out var value) && value is long id ? id : null;

    private static string? FirstLabel(string host)
    {
        var dot = host.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? host[..dot] : null;
    }
}
