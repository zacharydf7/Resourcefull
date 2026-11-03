using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Resourcefull.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class Resourcefull_InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResourceContainers",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "BINARY"),
                    Metadata = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceContainers", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "Resources",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ContainerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "BINARY"),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, collation: "BINARY"),
                    VersionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false, collation: "BINARY"),
                    LastModified = table.Column<long>(type: "INTEGER", nullable: false),
                    IsLatest = table.Column<bool>(type: "INTEGER", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: true),
                    ContentLength = table.Column<long>(type: "INTEGER", nullable: true),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: true),
                    Metadata = table.Column<string>(type: "text", nullable: false),
                    CopiedFromContainerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true, collation: "BINARY"),
                    CopiedFromName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true, collation: "BINARY"),
                    CopiedFromVersionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true, collation: "BINARY"),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DeclaredLength = table.Column<long>(type: "INTEGER", nullable: true),
                    StagedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Resources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Resources_ResourceContainers_ContainerName",
                        column: x => x.ContainerName,
                        principalTable: "ResourceContainers",
                        principalColumn: "Name",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResourceChunks",
                columns: table => new
                {
                    ResourceId = table.Column<long>(type: "INTEGER", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceChunks", x => new { x.ResourceId, x.Index });
                    table.ForeignKey(
                        name: "FK_ResourceChunks_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Resources_ExpiresAt",
                table: "Resources",
                columns: new[] { "ExpiresAt", "VersionId" },
                filter: "\"ExpiresAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_Latest",
                table: "Resources",
                columns: new[] { "ContainerName", "Name" },
                unique: true,
                filter: "\"IsLatest\"");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_StagedAt",
                table: "Resources",
                column: "StagedAt",
                filter: "\"StagedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Resources_Container_Name_Version",
                table: "Resources",
                columns: new[] { "ContainerName", "Name", "VersionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResourceChunks");

            migrationBuilder.DropTable(
                name: "Resources");

            migrationBuilder.DropTable(
                name: "ResourceContainers");
        }
    }
}
