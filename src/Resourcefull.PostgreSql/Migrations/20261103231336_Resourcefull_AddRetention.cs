using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Resourcefull.PostgreSql.Migrations
{
    /// <inheritdoc />
    internal partial class Resourcefull_AddRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LegalHold",
                table: "Resources",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetainUntil",
                table: "Resources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Resources_LegalHold",
                table: "Resources",
                column: "ContainerName",
                filter: "\"LegalHold\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_RetainUntil",
                table: "Resources",
                columns: new[] { "ContainerName", "RetainUntil" },
                filter: "\"RetainUntil\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Resources_LegalHold",
                table: "Resources");

            migrationBuilder.DropIndex(
                name: "IX_Resources_RetainUntil",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "LegalHold",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "RetainUntil",
                table: "Resources");
        }
    }
}
