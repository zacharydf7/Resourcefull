using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Resourcefull.SqlServer.Migrations
{
    /// <inheritdoc />
    internal partial class RedesignKeysAndVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ----------------------------------------------------------------------------------
            // 1. Add the new columns while the old keys are still in place, so existing data can
            //    be carried across before anything is dropped.
            // ----------------------------------------------------------------------------------

            // Surrogate identity for Resources. Adding an identity column auto-populates a unique
            // value for every existing row.
            migrationBuilder.AddColumn<long>(
                name: "Id",
                table: "Resources",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("SqlServer:Identity", "1, 1");

            // Current-version pointer. Backfilled below.
            migrationBuilder.AddColumn<bool>(
                name: "IsLatest",
                table: "Resources",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // New narrow foreign key for chunks. Backfilled below from the old composite key.
            migrationBuilder.AddColumn<long>(
                name: "ResourceId",
                table: "ResourceChunks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // ----------------------------------------------------------------------------------
            // 2. Backfill the new columns from existing data.
            // ----------------------------------------------------------------------------------

            // Point each chunk at its parent resource's new surrogate id.
            migrationBuilder.Sql("""
                EXEC(N'
                UPDATE c
                SET c.[ResourceId] = r.[Id]
                FROM [ResourceChunks] c
                INNER JOIN [Resources] r
                    ON c.[ResourceName] = r.[Name]
                   AND c.[ResourceContainerName] = r.[ContainerName]
                   AND c.[ResourceVersionId] = r.[VersionId];
                ');
                """);

            // Flag the most recent version of each (ContainerName, Name) as the current one,
            // matching the previous 'latest = max(LastModified)' read semantics.
            migrationBuilder.Sql("""
                EXEC(N'
                WITH latest AS (
                    SELECT [Id],
                           ROW_NUMBER() OVER (
                               PARTITION BY [ContainerName], [Name]
                               ORDER BY [LastModified] DESC, [Id] DESC) AS rn
                    FROM [Resources]
                )
                UPDATE r
                SET r.[IsLatest] = 1
                FROM [Resources] r
                INNER JOIN latest l ON r.[Id] = l.[Id]
                WHERE l.rn = 1;
                ');
                """);

            // ----------------------------------------------------------------------------------
            // 3. Drop the old keys/columns now that the data has been carried across.
            // ----------------------------------------------------------------------------------
            migrationBuilder.DropForeignKey(
                name: "FK_ResourceChunks_Resources_ResourceName_ResourceContainerName_ResourceVersionId",
                table: "ResourceChunks");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Resources",
                table: "Resources");

            migrationBuilder.DropIndex(
                name: "IX_Resources_ContainerName",
                table: "Resources");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ResourceChunks",
                table: "ResourceChunks");

            migrationBuilder.DropColumn(
                name: "ResourceName",
                table: "ResourceChunks");

            migrationBuilder.DropColumn(
                name: "ResourceContainerName",
                table: "ResourceChunks");

            migrationBuilder.DropColumn(
                name: "ResourceVersionId",
                table: "ResourceChunks");

            // ----------------------------------------------------------------------------------
            // 4. Establish the new keys, indexes and foreign key.
            // ----------------------------------------------------------------------------------

            // Surrogate PK kept NONCLUSTERED so the clustered index can match the access pattern.
            migrationBuilder.AddPrimaryKey(
                name: "PK_Resources",
                table: "Resources",
                column: "Id")
                .Annotation("SqlServer:Clustered", false);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ResourceChunks",
                table: "ResourceChunks",
                columns: new[] { "ResourceId", "Index" });

            migrationBuilder.CreateIndex(
                name: "IX_Resources_Latest",
                table: "Resources",
                columns: new[] { "ContainerName", "Name" },
                filter: "[IsLatest] = 1");

            // Cluster Resources on the natural key so a container's rows are physically contiguous.
            migrationBuilder.CreateIndex(
                name: "UX_Resources_Container_Name_Version",
                table: "Resources",
                columns: new[] { "ContainerName", "Name", "VersionId" },
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.AddForeignKey(
                name: "FK_ResourceChunks_Resources_ResourceId",
                table: "ResourceChunks",
                column: "ResourceId",
                principalTable: "Resources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ResourceChunks_Resources_ResourceId",
                table: "ResourceChunks");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Resources",
                table: "Resources");

            migrationBuilder.DropIndex(
                name: "IX_Resources_Latest",
                table: "Resources");

            migrationBuilder.DropIndex(
                name: "UX_Resources_Container_Name_Version",
                table: "Resources");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ResourceChunks",
                table: "ResourceChunks");

            // Re-create the old composite chunk key columns and backfill them from the surrogate
            // relationship before the surrogate columns are removed.
            migrationBuilder.AddColumn<string>(
                name: "ResourceName",
                table: "ResourceChunks",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ResourceContainerName",
                table: "ResourceChunks",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ResourceVersionId",
                table: "ResourceChunks",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(@"
                UPDATE c
                SET c.[ResourceName] = r.[Name],
                    c.[ResourceContainerName] = r.[ContainerName],
                    c.[ResourceVersionId] = r.[VersionId]
                FROM [ResourceChunks] c
                INNER JOIN [Resources] r ON c.[ResourceId] = r.[Id];");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "IsLatest",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "ResourceId",
                table: "ResourceChunks");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Resources",
                table: "Resources",
                columns: new[] { "Name", "ContainerName", "VersionId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_ResourceChunks",
                table: "ResourceChunks",
                columns: new[] { "ResourceName", "ResourceContainerName", "ResourceVersionId", "Index" });

            migrationBuilder.CreateIndex(
                name: "IX_Resources_ContainerName",
                table: "Resources",
                column: "ContainerName");

            migrationBuilder.AddForeignKey(
                name: "FK_ResourceChunks_Resources_ResourceName_ResourceContainerName_ResourceVersionId",
                table: "ResourceChunks",
                columns: new[] { "ResourceName", "ResourceContainerName", "ResourceVersionId" },
                principalTable: "Resources",
                principalColumns: new[] { "Name", "ContainerName", "VersionId" },
                onDelete: ReferentialAction.Cascade);
        }
    }
}
