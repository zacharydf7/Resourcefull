using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Resourcefull.Sqlite.Migrations
{
    /// <inheritdoc />
    internal partial class Resourcefull_AddMetadataSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResourceMetadata",
                columns: table => new
                {
                    ResourceId = table.Column<long>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "BINARY"),
                    ContainerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "BINARY"),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, collation: "BINARY"),
                    Value = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, collation: "BINARY")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceMetadata", x => new { x.ResourceId, x.Key });
                    table.ForeignKey(
                        name: "FK_ResourceMetadata_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceMetadata_Search",
                table: "ResourceMetadata",
                columns: new[] { "ContainerName", "Key", "Value", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResourceMetadata");
        }
    }
}
