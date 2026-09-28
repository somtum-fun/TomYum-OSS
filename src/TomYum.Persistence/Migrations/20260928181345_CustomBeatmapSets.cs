using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TomYum.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomBeatmapSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "custom_beatmap_slots",
                columns: table => new
                {
                    beatmap_id = table.Column<int>(type: "int", nullable: false),
                    set_id = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_beatmap_slots", x => x.beatmap_id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "custom_beatmapsets",
                columns: table => new
                {
                    set_id = table.Column<int>(type: "int", nullable: false),
                    owner_id = table.Column<int>(type: "int", nullable: false),
                    target = table.Column<int>(type: "int", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    uploaded_at = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_beatmapsets", x => x.set_id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_custom_beatmap_slots_set_id",
                table: "custom_beatmap_slots",
                column: "set_id");

            migrationBuilder.CreateIndex(
                name: "IX_custom_beatmapsets_owner_id",
                table: "custom_beatmapsets",
                column: "owner_id");

            // Custom sets uploaded before owners were kept: their ids stay taken. Their owner is set
            // by the next upload from the BN panel.
            migrationBuilder.Sql("INSERT IGNORE INTO custom_beatmap_slots (beatmap_id, set_id) SELECT Id, SetId FROM beatmaps WHERE Id >= 10000000");
            migrationBuilder.Sql(
                "INSERT IGNORE INTO custom_beatmapsets (set_id, owner_id, target, created_at, updated_at, uploaded_at) " +
                "SELECT SetId, 0, 0, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6) FROM beatmaps WHERE SetId >= 10000000 GROUP BY SetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "custom_beatmap_slots");

            migrationBuilder.DropTable(
                name: "custom_beatmapsets");
        }
    }
}
