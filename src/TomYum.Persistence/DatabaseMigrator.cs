using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TomYum.Persistence;

/// <summary>
/// Brings TomYum's database to the current schema with EF Core migrations (src/TomYum.Persistence/Migrations).
/// </summary>
/// <remarks>
/// Databases from before migrations were made by <c>EnsureCreated</c> (plus <see cref="SchemaGuard"/>)
/// and have no migration history. Their schema is the first migration's, so they're topped up with
/// SchemaGuard's tables and recorded as already at that migration (a baseline); later migrations
/// then apply to them like to any other database. Add a migration with
/// <c>dotnet ef migrations add &lt;Name&gt; --project src/TomYum.Persistence --context TomYumDbContext --output-dir Migrations</c>.
/// </remarks>
public static class DatabaseMigrator
{
    private const string HistoryTable = HistoryRepository.DefaultTableName;

    /// <summary>Applies pending migrations, baselining a pre-migrations database first. Returns the migrations applied.</summary>
    public static async Task<IReadOnlyList<string>> MigrateAsync(this TomYumDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
            && !await TableExistsAsync(db, HistoryTable, cancellationToken).ConfigureAwait(false)
            && await TableExistsAsync(db, "scores", cancellationToken).ConfigureAwait(false))
        {
            await BaselineAsync(db, cancellationToken).ConfigureAwait(false);
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        return pending;
    }

    /// <summary>Records a pre-migrations database as being at the first migration.</summary>
    private static async Task BaselineAsync(TomYumDbContext db, CancellationToken cancellationToken)
    {
        // Tables EnsureCreated didn't add to databases created before them.
        await db.EnsureRuntimeTablesAsync(cancellationToken).ConfigureAwait(false);

        var first = db.Database.GetMigrations().First();
        await db.Database.ExecuteSqlRawAsync(
            $"CREATE TABLE IF NOT EXISTS `{HistoryTable}` (`MigrationId` varchar(150) NOT NULL, `ProductVersion` varchar(32) NOT NULL, PRIMARY KEY (`MigrationId`)) CHARACTER SET=utf8mb4",
            cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT IGNORE INTO `{HistoryTable}` (`MigrationId`, `ProductVersion`) VALUES ({{0}}, {{1}})",
            [first, ProductInfo.GetVersion()], cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(TomYumDbContext db, string table, CancellationToken cancellationToken)
    {
        var count = await db.Database
            .SqlQuery<long>($"SELECT COUNT(*) AS Value FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = {table}")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        return count > 0;
    }
}
