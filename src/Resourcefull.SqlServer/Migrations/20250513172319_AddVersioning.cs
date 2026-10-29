using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Resourcefull.SqlServer.Migrations
{
    /// <inheritdoc />
    internal partial class AddVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ResourceChunks_Resources_ResourceName_ResourceContainerName",
                table: "ResourceChunks");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Resources",
                table: "Resources");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ResourceChunks",
                table: "ResourceChunks");

            migrationBuilder.AddColumn<string>(
                name: "VersionId",
                table: "Resources",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastModified",
                table: "Resources",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "ResourceVersionId",
                table: "ResourceChunks",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                EXEC(N'
                UPDATE [Resources]
                SET [VersionId] = FORMAT(GETUTCDATE(), ''yyyy-MM-dd''''T''''HH:mm:ss.fffffff''''Z''''''),
                    [LastModified] = GETUTCDATE()
                WHERE [VersionId] = '''';
                ');
                """);

            migrationBuilder.Sql("""
                EXEC(N'
                UPDATE [ResourceChunks]
                SET [ResourceVersionId] = (SELECT [VersionId] FROM [Resources] WHERE [Resources].[Name] = [ResourceChunks].[ResourceName] AND [Resources].[ContainerName] = [ResourceChunks].[ResourceContainerName])
                WHERE [ResourceVersionId] = '''';
                ');
                """);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Resources",
                table: "Resources",
                columns: new[] { "Name", "ContainerName", "VersionId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_ResourceChunks",
                table: "ResourceChunks",
                columns: new[] { "ResourceName", "ResourceContainerName", "ResourceVersionId", "Index" });

            migrationBuilder.AddForeignKey(
                name: "FK_ResourceChunks_Resources_ResourceName_ResourceContainerName_ResourceVersionId",
                table: "ResourceChunks",
                columns: new[] { "ResourceName", "ResourceContainerName", "ResourceVersionId" },
                principalTable: "Resources",
                principalColumns: new[] { "Name", "ContainerName", "VersionId" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ResourceChunks_Resources_ResourceName_ResourceContainerName_ResourceVersionId",
                table: "ResourceChunks");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Resources",
                table: "Resources");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ResourceChunks",
                table: "ResourceChunks");

            migrationBuilder.DropColumn(
                name: "VersionId",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "LastModified",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "ResourceVersionId",
                table: "ResourceChunks");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Resources",
                table: "Resources",
                columns: new[] { "Name", "ContainerName" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_ResourceChunks",
                table: "ResourceChunks",
                columns: new[] { "ResourceName", "ResourceContainerName", "Index" });

            migrationBuilder.AddForeignKey(
                name: "FK_ResourceChunks_Resources_ResourceName_ResourceContainerName",
                table: "ResourceChunks",
                columns: new[] { "ResourceName", "ResourceContainerName" },
                principalTable: "Resources",
                principalColumns: new[] { "Name", "ContainerName" },
                onDelete: ReferentialAction.Cascade);
        }
    }
}
