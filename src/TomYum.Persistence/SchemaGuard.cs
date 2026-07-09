using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence;

/// <summary>
/// Bridges the gap left by <see cref="Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade"/>'s
/// <c>EnsureCreated</c>, which is all-or-nothing: on an <em>already-created</em> database it will not add
/// tables introduced after the DB was first created. Until versioned migrations land, this applies
/// idempotent <c>CREATE TABLE IF NOT EXISTS</c> DDL for tables added later, so new features work on
/// existing dev/prod databases without a recreate. The DDL types match the EF model, so each statement
/// is a no-op on a fresh database that <c>EnsureCreated</c> already populated.
/// </summary>
public static class SchemaGuard
{
    // Added after the initial schema (achievements/medals). int / int / datetime(6), composite PK —
    // matches UserAchievementConfiguration + the DateTime UnlockedAt property.
    private const string UserAchievementsDdl =
        """
        CREATE TABLE IF NOT EXISTS `user_achievements` (
          `UserId` int NOT NULL,
          `AchievementId` int NOT NULL,
          `UnlockedAt` datetime(6) NOT NULL,
          PRIMARY KEY (`UserId`, `AchievementId`)
        ) CHARACTER SET=utf8mb4;
        """;

    /// <summary>Applies the idempotent DDL for tables added after the initial <c>EnsureCreated</c> schema.</summary>
    public static async Task EnsureRuntimeTablesAsync(this TomYumDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Database.ExecuteSqlRawAsync(UserAchievementsDdl, cancellationToken).ConfigureAwait(false);
    }
}
