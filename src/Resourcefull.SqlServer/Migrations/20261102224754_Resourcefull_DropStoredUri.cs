using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Resourcefull.SqlServer.Migrations
{
    /// <inheritdoc />
    internal partial class Resourcefull_DropStoredUri : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Uri",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "Uri",
                table: "ResourceContainers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Uri",
                table: "Resources",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Uri",
                table: "ResourceContainers",
                type: "nvarchar(max)",
                nullable: true);
        }
    }
}
