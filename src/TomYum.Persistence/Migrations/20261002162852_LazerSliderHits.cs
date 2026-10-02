using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TomYum.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LazerSliderHits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LargeTickHits",
                table: "scores",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SliderEndHits",
                table: "scores",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LargeTickHits",
                table: "scores");

            migrationBuilder.DropColumn(
                name: "SliderEndHits",
                table: "scores");
        }
    }
}
