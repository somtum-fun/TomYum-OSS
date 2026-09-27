using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TomYum.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "beatmaps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    SetId = table.Column<int>(type: "int", nullable: false),
                    Md5 = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Artist = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Title = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Version = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Creator = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StatusFrozen = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Mode = table.Column<int>(type: "int", nullable: false),
                    StarRating = table.Column<double>(type: "double", nullable: false),
                    TotalLength = table.Column<int>(type: "int", nullable: false),
                    MaxCombo = table.Column<int>(type: "int", nullable: false),
                    LastUpdate = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_beatmaps", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "chat_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    SentAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    SenderId = table.Column<int>(type: "int", nullable: false),
                    SenderName = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Target = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RecipientId = table.Column<int>(type: "int", nullable: true),
                    Text = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_messages", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "scores",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    BeatmapId = table.Column<int>(type: "int", nullable: false),
                    BeatmapMd5 = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Mode = table.Column<int>(type: "int", nullable: false),
                    ScoreValue = table.Column<long>(type: "bigint", nullable: false),
                    MaxCombo = table.Column<int>(type: "int", nullable: false),
                    Count300 = table.Column<int>(type: "int", nullable: false),
                    Count100 = table.Column<int>(type: "int", nullable: false),
                    Count50 = table.Column<int>(type: "int", nullable: false),
                    CountMiss = table.Column<int>(type: "int", nullable: false),
                    CountGeki = table.Column<int>(type: "int", nullable: false),
                    CountKatu = table.Column<int>(type: "int", nullable: false),
                    Grade = table.Column<string>(type: "varchar(2)", maxLength: 2, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Mods = table.Column<int>(type: "int", nullable: false),
                    Perfect = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Pp = table.Column<double>(type: "double", nullable: false),
                    Passed = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scores", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "staff_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ActorId = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TargetId = table.Column<long>(type: "bigint", nullable: true),
                    Summary = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Result = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    JobId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_log", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "user_achievements",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false),
                    AchievementId = table.Column<int>(type: "int", nullable: false),
                    UnlockedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_achievements", x => new { x.UserId, x.AchievementId });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "user_stats",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Mode = table.Column<int>(type: "int", nullable: false),
                    RankedScore = table.Column<long>(type: "bigint", nullable: false),
                    TotalScore = table.Column<long>(type: "bigint", nullable: false),
                    PlayCount = table.Column<int>(type: "int", nullable: false),
                    Accuracy = table.Column<float>(type: "float", nullable: false),
                    Pp = table.Column<double>(type: "double", nullable: false),
                    GlobalRank = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_stats", x => new { x.UserId, x.Mode });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Username = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SafeName = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Email = table.Column<string>(type: "varchar(254)", maxLength: 254, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PasswordBcrypt = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Country = table.Column<string>(type: "varchar(2)", maxLength: 2, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreationTime = table.Column<long>(type: "bigint", nullable: false),
                    LatestActivity = table.Column<long>(type: "bigint", nullable: false),
                    ApiKey = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Privileges = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_beatmaps_Md5",
                table: "beatmaps",
                column: "Md5",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_beatmaps_SetId",
                table: "beatmaps",
                column: "SetId");

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_RecipientId_Id",
                table: "chat_messages",
                columns: new[] { "RecipientId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_SenderId_Id",
                table: "chat_messages",
                columns: new[] { "SenderId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_SentAt",
                table: "chat_messages",
                column: "SentAt");

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_Target_Id",
                table: "chat_messages",
                columns: new[] { "Target", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_scores_BeatmapMd5_Mode_Status_ScoreValue",
                table: "scores",
                columns: new[] { "BeatmapMd5", "Mode", "Status", "ScoreValue" });

            migrationBuilder.CreateIndex(
                name: "IX_scores_UserId_BeatmapMd5_Mode_Status",
                table: "scores",
                columns: new[] { "UserId", "BeatmapMd5", "Mode", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_staff_log_Action_Id",
                table: "staff_log",
                columns: new[] { "Action", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_staff_log_JobId",
                table: "staff_log",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_staff_log_TargetId",
                table: "staff_log",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_users_ApiKey",
                table: "users",
                column: "ApiKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_SafeName",
                table: "users",
                column: "SafeName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "beatmaps");

            migrationBuilder.DropTable(
                name: "chat_messages");

            migrationBuilder.DropTable(
                name: "scores");

            migrationBuilder.DropTable(
                name: "staff_log");

            migrationBuilder.DropTable(
                name: "user_achievements");

            migrationBuilder.DropTable(
                name: "user_stats");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
