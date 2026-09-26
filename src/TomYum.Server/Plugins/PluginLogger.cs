using Microsoft.Extensions.Logging;

using TomYum.Plugins.Abstractions;

namespace TomYum.Server.Plugins;

/// <summary>Adapts the host <see cref="ILogger"/> to the plugin-facing <see cref="IPluginLogger"/>.</summary>
internal sealed class PluginLogger : IPluginLogger
{
    private readonly ILogger _logger;

    public PluginLogger(ILogger logger) => _logger = logger;

    public void Info(string message) => _logger.LogInformation("{Message}", message);

    public void Warn(string message) => _logger.LogWarning("{Message}", message);

    public void Error(string message, Exception? exception = null) => _logger.LogError(exception, "{Message}", message);
}
