using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Resourcefull.SqlServer.Migrations
{
    /// <inheritdoc />
    internal partial class AddCopiedFrom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CopiedFromContainerName",
                table: "Resources",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "CopiedFromName",
                table: "Resources",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "CopiedFromVersionId",
                table: "Resources",
                type: "nvarchar(36)",
                maxLength: 36,
                nullable: true,
                collation: "Latin1_General_100_BIN2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CopiedFromContainerName",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "CopiedFromName",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "CopiedFromVersionId",
                table: "Resources");
        }
    }
}
