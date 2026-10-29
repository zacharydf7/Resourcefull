using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Resourcefull.SqlServer.Migrations
{
    /// <inheritdoc />
    internal partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResourceContainers",
                columns: table => new
                {
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Uri = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Metadata = table.Column<string>(type: "varchar(MAX)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceContainers", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "Resources",
                columns: table => new
                {
                    ContainerName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ContentLength = table.Column<long>(type: "bigint", nullable: true),
                    Uri = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Metadata = table.Column<string>(type: "varchar(MAX)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Resources", x => new { x.Name, x.ContainerName });
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
                    ResourceContainerName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ResourceName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Index = table.Column<int>(type: "int", nullable: false),
                    Data = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceChunks", x => new { x.ResourceName, x.ResourceContainerName, x.Index });
                    table.ForeignKey(
                        name: "FK_ResourceChunks_Resources_ResourceName_ResourceContainerName",
                        columns: x => new { x.ResourceName, x.ResourceContainerName },
                        principalTable: "Resources",
                        principalColumns: new[] { "Name", "ContainerName" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Resources_ContainerName",
                table: "Resources",
                column: "ContainerName");
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
