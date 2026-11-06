using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Resourcefull.SqlServer;

/// <summary>
/// How Resourcefull uses SQL Server.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class ResourcefullSqlServerOptions
{
    /// <summary>
    /// The schema that holds Resourcefull's tables, such as <c>resourcefull</c>, so they don't mix with an application's
    /// own tables in <c>dbo</c>. Resourcefull's migration history is then kept in <c>__EFMigrationsHistory</c> in that
    /// schema too. <see langword="null"/>, the default, leaves the tables' names unqualified, as in 3.x, so they're in
    /// the database user's default schema, normally <c>dbo</c>. It must be a regular identifier of up to 128 characters:
    /// a letter or underscore, then letters, digits or underscores.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>
    /// Changes EF Core's SQL Server options, after Resourcefull has set its own: the command timeout or the retries, for
    /// example. Replacing the retry policy, such as with <c>EnableRetryOnFailure</c>, also stops Resourcefull from counting
    /// retries in <c>resourcefull.sqlserver.retries</c>; give it error 50001 to retry, which a write whose lock on its
    /// resource name lost a deadlock raises.
    /// </summary>
    public Action<SqlServerDbContextOptionsBuilder>? ConfigureSqlServer { get; set; }
}
