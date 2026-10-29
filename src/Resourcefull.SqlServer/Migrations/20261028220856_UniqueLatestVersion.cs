using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Resourcefull.SqlServer.Migrations
{
    /// <inheritdoc />
    internal partial class UniqueLatestVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Resources_Latest",
                table: "Resources");

            // Concurrent uploads before this index existed could leave a name with more than one current version.
            // Keep the most recently stored one current so the unique index can be created.
            migrationBuilder.Sql("""
                UPDATE r
                SET r.[IsLatest] = 0
                FROM [Resources] r
                WHERE r.[IsLatest] = 1
                  AND EXISTS (
                      SELECT 1 FROM [Resources] newer
                      WHERE newer.[ContainerName] = r.[ContainerName]
                        AND newer.[Name] = r.[Name]
                        AND newer.[IsLatest] = 1
                        AND newer.[Id] > r.[Id]);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Resources_Latest",
                table: "Resources",
                columns: new[] { "ContainerName", "Name" },
                unique: true,
                filter: "[IsLatest] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Resources_Latest",
                table: "Resources");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_Latest",
                table: "Resources",
                columns: new[] { "ContainerName", "Name" },
                filter: "[IsLatest] = 1");
        }
    }
}
