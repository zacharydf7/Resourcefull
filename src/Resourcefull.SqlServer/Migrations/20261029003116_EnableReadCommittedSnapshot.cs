using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Resourcefull.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class EnableReadCommittedSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Row-versioned reads remove the reader/writer deadlock class. ALTER DATABASE can't run inside the migration
            // transaction, so this migration holds nothing else: if it fails nothing has changed and it can simply be
            // run again. ROLLBACK IMMEDIATE forces other connections off, so it is skipped when already on (as on
            // databases upgraded by earlier releases, which enabled it with the key redesign) and otherwise needs a
            // maintenance window.
            migrationBuilder.Sql("""
                IF (SELECT [is_read_committed_snapshot_on] FROM sys.databases WHERE [database_id] = DB_ID()) = 0
                    ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
                """,
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF (SELECT [is_read_committed_snapshot_on] FROM sys.databases WHERE [database_id] = DB_ID()) = 1
                    ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE;
                """,
                suppressTransaction: true);
        }
    }
}
