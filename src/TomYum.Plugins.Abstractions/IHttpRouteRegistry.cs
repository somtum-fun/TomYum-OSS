namespace TomYum.Plugins.Abstractions;

/// <summary>
/// Lets a plugin contribute HTTP endpoints to the host (the Session plugin uses this
/// to mount its frontend API surface). The host owns the actual web stack; this
/// abstraction keeps plugins free of a direct ASP.NET dependency.
/// </summary>
public interface IHttpRouteRegistry
{
    /// <summary>
    /// Maps an HTTP route to a handler.
    /// </summary>
    /// <param name="method">HTTP method, e.g. <c>GET</c> or <c>POST</c>.</param>
    /// <param name="pattern">Route template, e.g. <c>/api/v1/clans/{id}</c>.</param>
    /// <param name="handler">Handles a <see cref="PluginHttpRequest"/> and returns a <see cref="PluginHttpResponse"/>.</param>
    /// <param name="requireAuth">When <c>true</c>, the host enforces a valid session before invoking the handler.</param>
    /// <returns>A disposable that removes the route when disposed.</returns>
    IDisposable Map(
        string method,
        string pattern,
        Func<PluginHttpRequest, CancellationToken, Task<PluginHttpResponse>> handler,
        bool requireAuth = false);
}

/// <summary>A framework-agnostic view of an incoming HTTP request passed to a plugin route handler.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Path">Request path.</param>
/// <param name="RouteValues">Values captured from the route template.</param>
/// <param name="Query">Query-string parameters.</param>
/// <param name="Headers">Request headers.</param>
/// <param name="Body">Raw request body stream, or <c>null</c> when empty.</param>
/// <param name="AuthenticatedUserId">Resolved user id when the request carried a valid session, else <c>null</c>.</param>
public sealed record PluginHttpRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> RouteValues,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers,
    Stream? Body,
    long? AuthenticatedUserId);

/// <summary>A framework-agnostic HTTP response returned by a plugin route handler.</summary>
/// <param name="StatusCode">HTTP status code.</param>
/// <param name="ContentType">Response MIME type.</param>
/// <param name="Body">Response body bytes.</param>
/// <param name="Headers">Optional extra response headers.</param>
public sealed record PluginHttpResponse(
    int StatusCode,
    string ContentType,
    ReadOnlyMemory<byte> Body,
    IReadOnlyDictionary<string, string>? Headers = null);
