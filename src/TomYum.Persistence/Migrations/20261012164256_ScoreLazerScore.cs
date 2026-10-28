using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TomYum.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScoreLazerScore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LazerScore",
                table: "scores",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LazerScore",
                table: "scores");
        }
    }
}
