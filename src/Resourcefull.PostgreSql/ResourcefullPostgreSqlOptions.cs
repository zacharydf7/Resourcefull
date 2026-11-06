using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Resourcefull.PostgreSql;

/// <summary>
/// How Resourcefull uses PostgreSQL.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class ResourcefullPostgreSqlOptions
{
    /// <summary>
    /// The schema that holds Resourcefull's tables, such as <c>resourcefull</c>, so they don't mix with an application's
    /// own tables in <c>public</c>. Resourcefull's migration history is then kept in <c>__EFMigrationsHistory</c> in that
    /// schema too. <see langword="null"/>, the default, leaves the tables' names unqualified, so they're found through the
    /// connection's search path, normally in <c>public</c>. It must be a regular identifier of up to 63 characters: a
    /// letter or underscore, then letters, digits or underscores.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>
    /// Changes EF Core's PostgreSQL options, after Resourcefull has set its own: the command timeout or the retries, for
    /// example. Replacing the retry policy, such as with <c>EnableRetryOnFailure</c>, also stops Resourcefull from
    /// counting retries in <c>resourcefull.postgresql.retries</c>.
    /// </summary>
    public Action<NpgsqlDbContextOptionsBuilder>? ConfigurePostgreSql { get; set; }
}
