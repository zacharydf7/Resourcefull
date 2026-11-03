using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Resourcefull.PostgreSql.Migrations
{
    /// <inheritdoc />
    internal partial class Resourcefull_InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResourceContainers",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
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
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContainerName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    VersionId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false, collation: "C"),
                    LastModified = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsLatest = table.Column<bool>(type: "boolean", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: true),
                    ContentLength = table.Column<long>(type: "bigint", nullable: true),
                    ContentHash = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: true),
                    Metadata = table.Column<string>(type: "text", nullable: false),
                    CopiedFromContainerName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true, collation: "C"),
                    CopiedFromName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true, collation: "C"),
                    CopiedFromVersionId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true, collation: "C"),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StagedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
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
                    ResourceId = table.Column<long>(type: "bigint", nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false)
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
                filter: "\"StagedAt\" IS NOT NULL")
                .Annotation("Npgsql:IndexInclude", new[] { "Id" });

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
