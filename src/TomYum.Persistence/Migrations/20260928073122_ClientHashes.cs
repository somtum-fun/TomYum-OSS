using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TomYum.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClientHashes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "client_hashes",
                columns: table => new
                {
                    user_id = table.Column<int>(type: "int", nullable: false),
                    osupath = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    adapters = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    uninstall_id = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    disk_serial = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    latest_time = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    occurrences = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_hashes", x => new { x.user_id, x.osupath, x.adapters, x.uninstall_id, x.disk_serial });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "verified_users",
                columns: table => new
                {
                    user_id = table.Column<int>(type: "int", nullable: false),
                    verified_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    verified_by = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_verified_users", x => x.user_id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_client_hashes_adapters",
                table: "client_hashes",
                column: "adapters");

            migrationBuilder.CreateIndex(
                name: "IX_client_hashes_disk_serial",
                table: "client_hashes",
                column: "disk_serial");

            migrationBuilder.CreateIndex(
                name: "IX_client_hashes_uninstall_id",
                table: "client_hashes",
                column: "uninstall_id");

            // Players who already played here are past the first-login check.
            migrationBuilder.Sql("INSERT IGNORE INTO verified_users (user_id, verified_at, verified_by) SELECT DISTINCT UserId, UTC_TIMESTAMP(6), 0 FROM scores");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "client_hashes");

            migrationBuilder.DropTable(
                name: "verified_users");
        }
    }
}
