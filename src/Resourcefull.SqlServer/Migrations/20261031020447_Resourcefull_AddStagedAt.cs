using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Resourcefull.SqlServer.Migrations
{
    /// <inheritdoc />
    internal partial class Resourcefull_AddStagedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StagedAt",
                table: "Resources",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Resources_StagedAt",
                table: "Resources",
                column: "StagedAt",
                filter: "[StagedAt] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Resources_StagedAt",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "StagedAt",
                table: "Resources");
        }
    }
}
