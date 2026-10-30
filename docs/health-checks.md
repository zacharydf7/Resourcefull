# Health checks

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>

Resourcefull adds checks to .NET's [health checks](https://learn.microsoft.com/aspnet/core/host-and-deploy/health-checks),
so a load balancer, an orchestrator or your monitoring can tell whether an instance can serve files.

```csharp
builder.Services.AddHealthChecks()
    .AddResourcefullSqlServerCheck(tags: ["ready"]);  // with SQL Server

// or, with any provider:
builder.Services.AddHealthChecks()
    .AddResourcefullCheck(tags: ["ready"]);

app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
```

Both take the usual parameters: `name` (`"resourcefull"` by default), `failureStatus`, `tags` and `timeout`.

## What they report

**`AddResourcefullCheck()`** makes one read through the configured provider that changes nothing:

| Status | When |
|---|---|
| Healthy | the read succeeds |
| Unhealthy, or your `failureStatus` | the read fails or times out; the report carries the exception |

**`AddResourcefullSqlServerCheck()`** reads the database's migration history, which also shows it can be reached:

| Status | When |
|---|---|
| Healthy | the database has every migration this release needs |
| Unhealthy, or your `failureStatus` | the database can't be reached, or it's missing migrations, which the report names: this release's queries would fail against the older schema |
| Degraded | the database has migrations of Resourcefull's that this release doesn't know (your own EF Core migrations in the same database are ignored): a newer release upgraded it first, which is a [supported rollout](upgrading.md#upgrading-a-running-application) |

A report that the check completed includes the provider in its data, and for SQL Server the `pendingMigrations` and
`unknownMigrations`. When the check fails or times out, the report carries the exception instead.

## Where they help

- **The database goes down or fails over.** Each instance reports Unhealthy within seconds, your monitoring alerts
  on it, and traffic resumes by itself when the checks pass again.
- **A deploy runs ahead of its migration.** The new instances report Unhealthy because migrations are pending, so
  the load balancer keeps them out of rotation while the old ones keep serving, and the deployment stops at the failed
  readiness check.
- **The schema is upgraded before the application.** The old instances report Degraded: still serving, with the
  database ahead of them visible on a dashboard.

## Readiness, not liveness

Use these checks to decide whether an instance gets traffic (readiness), not whether it gets restarted (liveness).
When the database is down or behind, restarting the application doesn't help, and a failing liveness check would make
the platform restart every instance in a loop.

## Timeouts

With SQL Server, a connection that fails is retried for transient errors, which can take tens of seconds. Pass a
`timeout` shorter than your probe's interval, such as `TimeSpan.FromSeconds(5)`, so a check reports Unhealthy
promptly instead of waiting out the retries.

## Telemetry

Each run records a `resourcefull health_check` span, and for `AddResourcefullCheck` a duration, like any other
operation; see [Telemetry](telemetry.md).
