using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TomYum.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LazerScoreLinkAndHeldPp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "OsuWebScoreId",
                table: "scores",
                type: "bigint unsigned",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PpHeld",
                table: "scores",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_scores_OsuWebScoreId",
                table: "scores",
                column: "OsuWebScoreId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_scores_OsuWebScoreId",
                table: "scores");

            migrationBuilder.DropColumn(
                name: "OsuWebScoreId",
                table: "scores");

            migrationBuilder.DropColumn(
                name: "PpHeld",
                table: "scores");
        }
    }
}
