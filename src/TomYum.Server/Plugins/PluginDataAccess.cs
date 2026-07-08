using System.Data.Common;

using MySqlConnector;

using TomYum.Plugins.Abstractions;

namespace TomYum.Server.Plugins;

/// <summary>
/// Trusted raw DB access (<see cref="IPluginDataAccess"/>). Handed only to plugins the operator
/// marks trusted (ADR-0007) so they can own their own tables (the Session plugin's web tables)
/// without a persistence-package dependency. Each call opens a fresh connection the caller disposes.
/// </summary>
internal sealed class PluginDataAccess : IPluginDataAccess
{
    private readonly string _connectionString;

    public PluginDataAccess(string connectionString) => _connectionString = connectionString;

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
