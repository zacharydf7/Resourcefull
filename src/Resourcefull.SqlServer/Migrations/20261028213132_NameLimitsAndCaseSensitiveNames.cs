using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Resourcefull.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class NameLimitsAndCaseSensitiveNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EF rebuilds the indexes on altered columns but not keys, and SQL Server refuses to alter a column a key
            // depends on, so the container key and the foreign key onto it are dropped first and recreated last.
            DropContainerKeys(migrationBuilder);

            migrationBuilder.AlterColumn<string>(
                name: "VersionId",
                table: "Resources",
                type: "nvarchar(36)",
                maxLength: 36,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Resources",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AlterColumn<string>(
                name: "ContainerName",
                table: "Resources",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "ResourceContainers",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            AddContainerKeys(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DropContainerKeys(migrationBuilder);

            migrationBuilder.AlterColumn<string>(
                name: "VersionId",
                table: "Resources",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(36)",
                oldMaxLength: 36,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Resources",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AlterColumn<string>(
                name: "ContainerName",
                table: "Resources",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "ResourceContainers",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldCollation: "Latin1_General_100_BIN2");

            AddContainerKeys(migrationBuilder);
        }

        private static void DropContainerKeys(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Resources_ResourceContainers_ContainerName",
                table: "Resources");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ResourceContainers",
                table: "ResourceContainers");
        }

        private static void AddContainerKeys(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddPrimaryKey(
                name: "PK_ResourceContainers",
                table: "ResourceContainers",
                column: "Name");

            migrationBuilder.AddForeignKey(
                name: "FK_Resources_ResourceContainers_ContainerName",
                table: "Resources",
                column: "ContainerName",
                principalTable: "ResourceContainers",
                principalColumn: "Name",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
