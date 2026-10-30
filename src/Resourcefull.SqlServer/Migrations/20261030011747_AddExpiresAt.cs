using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Resourcefull.SqlServer.Migrations
{
    /// <inheritdoc />
    internal partial class AddExpiresAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "Resources",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Resources_ExpiresAt",
                table: "Resources",
                columns: new[] { "ExpiresAt", "VersionId" },
                filter: "[ExpiresAt] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Resources_ExpiresAt",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "Resources");
        }
    }
}
