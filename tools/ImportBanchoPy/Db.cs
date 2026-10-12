using System.Globalization;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace TomYum.Tools.ImportBanchoPy;

/// <summary>
/// One MySQL connection the importer runs its statements on. The three databases (bancho.py's
/// copy, TomYum's, osu-web's) are on the same server, so everything is plain SQL with qualified
/// names: <c>INSERT ... SELECT</c> across schemas does the bulk of the work.
/// </summary>
/// <remarks>
/// SQL text is built from fixed statements, schema and table names checked by
/// <see cref="Identifier"/>, and numbers formatted invariantly; anything that came from a player
/// (names, reasons) travels as a parameter. That is why CA2100 is off here and nowhere else.
/// </remarks>
internal sealed class Db : IAsyncDisposable
{
    private static readonly Regex IdentifierPattern = new("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant);

    private readonly MySqlConnection _connection;

    public Db(string connectionString)
    {
        _connection = new MySqlConnection(connectionString);
    }

    public MySqlConnection Connection => _connection;

    public Task OpenAsync() => _connection.OpenAsync();

    /// <summary>A schema or table name that is safe to splice into SQL, backticked.</summary>
    public static string Identifier(string name)
    {
        if (!IdentifierPattern.IsMatch(name))
            throw new ArgumentException($"'{name}' isn't a plain database name (letters, digits and underscores).", nameof(name));
        return "`" + name + "`";
    }

    /// <summary>A number for SQL text.</summary>
    public static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

#pragma warning disable CA2100 // see the class remarks
    public async Task<int> ExecAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, _connection) { CommandTimeout = 0 };
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, _connection) { CommandTimeout = 0 };
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        if (result is null or DBNull)
            return default;
        return (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }

    public async Task<long> CountAsync(string sql, params (string Name, object? Value)[] parameters) =>
        await ScalarAsync<long?>(sql, parameters).ConfigureAwait(false) ?? 0;

    public async Task<List<T>> QueryAsync<T>(string sql, Func<MySqlDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, _connection) { CommandTimeout = 0 };
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var rows = new List<T>();
        while (await reader.ReadAsync().ConfigureAwait(false))
            rows.Add(map(reader));
        return rows;
    }
#pragma warning restore CA2100

    /// <summary>Whether <paramref name="table"/> exists in <paramref name="schema"/>.</summary>
    public Task<long> TableExistsAsync(string schema, string table) =>
        CountAsync("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @s AND table_name = @t", ("@s", schema), ("@t", table));

    /// <summary>Whether <paramref name="column"/> exists on <paramref name="schema"/>.<paramref name="table"/>.</summary>
    public Task<long> ColumnExistsAsync(string schema, string table, string column) =>
        CountAsync("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = @s AND table_name = @t AND column_name = @c",
            ("@s", schema), ("@t", table), ("@c", column));

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
