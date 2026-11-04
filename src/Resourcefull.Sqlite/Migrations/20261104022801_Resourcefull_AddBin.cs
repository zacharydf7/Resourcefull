using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Resourcefull.Sqlite.Migrations
{
    /// <inheritdoc />
    internal partial class Resourcefull_AddBin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DeletedAt",
                table: "Resources",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PurgeAt",
                table: "Resources",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Resources_DeletedAt",
                table: "Resources",
                columns: new[] { "ContainerName", "DeletedAt", "VersionId" },
                descending: new[] { false, true, false },
                filter: "\"DeletedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_PurgeAt",
                table: "Resources",
                columns: new[] { "PurgeAt", "VersionId" },
                filter: "\"PurgeAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Resources_DeletedAt",
                table: "Resources");

            migrationBuilder.DropIndex(
                name: "IX_Resources_PurgeAt",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "PurgeAt",
                table: "Resources");
        }
    }
}
