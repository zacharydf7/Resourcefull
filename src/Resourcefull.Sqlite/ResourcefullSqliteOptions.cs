using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Resourcefull.Sqlite;

/// <summary>
/// How Resourcefull uses SQLite.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class ResourcefullSqliteOptions
{
    /// <summary>
    /// Changes EF Core's SQLite options, after Resourcefull has set its own: the command timeout, for example, which is
    /// how long a write waits for another to finish before it fails, 30 seconds by default.
    /// </summary>
    public Action<SqliteDbContextOptionsBuilder>? ConfigureSqlite { get; set; }
}
