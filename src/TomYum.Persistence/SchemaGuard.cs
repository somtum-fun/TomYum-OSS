using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence;

/// <summary>
/// Bridges the gap left by <see cref="Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade"/>'s
/// <c>EnsureCreated</c>, which is all-or-nothing: on an <em>already-created</em> database it will not add
/// tables introduced after the DB was first created. Before EF migrations (see DatabaseMigrator), this applied
/// idempotent <c>CREATE TABLE IF NOT EXISTS</c> DDL for tables added later; now it only tops up
/// pre-migrations databases before they are baselined. The DDL types match the EF model, so each statement
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

    // Staff panel action log. Matches StaffLogEntryConfiguration (EF's default index names).
    private const string StaffLogDdl =
        """
        CREATE TABLE IF NOT EXISTS `staff_log` (
          `Id` bigint NOT NULL AUTO_INCREMENT,
          `CreatedAt` datetime(6) NOT NULL,
          `ActorId` int NOT NULL,
          `Action` varchar(64) NOT NULL,
          `TargetId` bigint NULL,
          `Summary` varchar(512) NOT NULL,
          `Result` varchar(32) NULL,
          `JobId` varchar(32) NULL,
          PRIMARY KEY (`Id`),
          KEY `IX_staff_log_Action_Id` (`Action`, `Id`),
          KEY `IX_staff_log_TargetId` (`TargetId`),
          KEY `IX_staff_log_JobId` (`JobId`)
        ) CHARACTER SET=utf8mb4;
        """;

    // In-game chat log. Matches ChatLogEntryConfiguration (EF's default index names).
    private const string ChatMessagesDdl =
        """
        CREATE TABLE IF NOT EXISTS `chat_messages` (
          `Id` bigint NOT NULL AUTO_INCREMENT,
          `SentAt` datetime(6) NOT NULL,
          `SenderId` int NOT NULL,
          `SenderName` varchar(32) NOT NULL,
          `Target` varchar(64) NOT NULL,
          `RecipientId` int NULL,
          `Text` varchar(2048) NOT NULL,
          PRIMARY KEY (`Id`),
          KEY `IX_chat_messages_SentAt` (`SentAt`),
          KEY `IX_chat_messages_SenderId_Id` (`SenderId`, `Id`),
          KEY `IX_chat_messages_RecipientId_Id` (`RecipientId`, `Id`),
          KEY `IX_chat_messages_Target_Id` (`Target`, `Id`)
        ) CHARACTER SET=utf8mb4;
        """;

    /// <summary>Applies the idempotent DDL for tables added after the initial <c>EnsureCreated</c> schema.</summary>
    public static async Task EnsureRuntimeTablesAsync(this TomYumDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Database.ExecuteSqlRawAsync(UserAchievementsDdl, cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(StaffLogDdl, cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(ChatMessagesDdl, cancellationToken).ConfigureAwait(false);
    }
}
