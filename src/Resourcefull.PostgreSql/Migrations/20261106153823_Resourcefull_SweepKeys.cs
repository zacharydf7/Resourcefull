using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Resourcefull.PostgreSql.Migrations
{
    /// <inheritdoc />
    internal partial class Resourcefull_SweepKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Resources_DeletedAt",
                table: "Resources");

            migrationBuilder.DropIndex(
                name: "IX_Resources_ExpiresAt",
                table: "Resources");

            migrationBuilder.DropIndex(
                name: "IX_Resources_PurgeAt",
                table: "Resources");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_DeletedAt",
                table: "Resources",
                columns: new[] { "ContainerName", "DeletedAt", "VersionId", "Name" },
                descending: new[] { false, true, false, false },
                filter: "\"DeletedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_ExpiresAt",
                table: "Resources",
                columns: new[] { "ExpiresAt", "VersionId", "ContainerName", "Name" },
                filter: "\"ExpiresAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_PurgeAt",
                table: "Resources",
                columns: new[] { "PurgeAt", "VersionId", "ContainerName", "Name" },
                filter: "\"PurgeAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Resources_DeletedAt",
                table: "Resources");

            migrationBuilder.DropIndex(
                name: "IX_Resources_ExpiresAt",
                table: "Resources");

            migrationBuilder.DropIndex(
                name: "IX_Resources_PurgeAt",
                table: "Resources");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_DeletedAt",
                table: "Resources",
                columns: new[] { "ContainerName", "DeletedAt", "VersionId" },
                descending: new[] { false, true, false },
                filter: "\"DeletedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_ExpiresAt",
                table: "Resources",
                columns: new[] { "ExpiresAt", "VersionId" },
                filter: "\"ExpiresAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_PurgeAt",
                table: "Resources",
                columns: new[] { "PurgeAt", "VersionId" },
                filter: "\"PurgeAt\" IS NOT NULL");
        }
    }
}
