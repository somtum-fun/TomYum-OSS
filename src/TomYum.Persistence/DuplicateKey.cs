using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence;

/// <summary>
/// "Insert if it isn't there" races: two requests both find no row and both insert it (two imports of a
/// beatmap set at once, say). The second gets MySQL's duplicate key error; trying again finds the row
/// and updates it instead.
/// </summary>
internal static class DuplicateKey
{
    /// <summary>Whether <paramref name="ex"/> is MySQL refusing a row whose key is taken.</summary>
    public static bool Is(DbUpdateException ex) =>
        ex.InnerException is MySqlConnector.MySqlException { ErrorCode: MySqlConnector.MySqlErrorCode.DuplicateKeyEntry };

    /// <summary>
    /// Runs <paramref name="write"/> (which looks the row up, then adds or changes it, then saves), once
    /// more when another request inserted the same row in between.
    /// </summary>
    public static async Task RetryAsync(DbContext db, Func<Task> write)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (Is(ex))
        {
            db.ChangeTracker.Clear();
            await write().ConfigureAwait(false);
        }
    }
}
