# Aspire

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>

[.NET Aspire](https://learn.microsoft.com/dotnet/aspire/get-started/aspire-overview) runs your application and the
services it needs together during development, and wires up telemetry and health checks. Resourcefull needs no
package of its own for it: a SQL Server resource for the database, and two lines each for telemetry and health.

## The app host

Add a SQL Server and a database to the app host, and pass the database to the project that uses Resourcefull:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<SqlServerDatabaseResource> resources = builder.AddSqlServer("sql").AddDatabase("resources");
builder.AddProject<Projects.Files>("files").WithReference(resources).WaitFor(resources).WithHttpHealthCheck("/health");

builder.Build().Run();
```

`WithReference` gives the project a connection string named `resources`, and `WithHttpHealthCheck` shows the
project's health in the dashboard.

## The project

Read that connection string, and provision the database at startup, which suits development; in production, provision
from a deployment step instead (see [Provisioning](providers/sql-server.md#provisioning)):

```csharp
builder.AddServiceDefaults();
builder.Services.AddResourcefull(options => options.UseSqlServer(builder.Configuration.GetConnectionString("resources")!));
builder.Services.AddHealthChecks().AddResourcefullSqlServerCheck();

var app = builder.Build();
app.MapDefaultEndpoints();
await app.Services.MigrateResourcefullDatabaseAsync();
```

The health check joins the others at `/health`, which the dashboard watches. The service defaults template maps
`/health` only in Development; in production, map a readiness endpoint for your orchestrator as
[Health checks](health-checks.md) shows.

## Telemetry

The service defaults project that Aspire's templates create sets up OpenTelemetry. Add Resourcefull's source and meter
to it, in `ConfigureOpenTelemetry`:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("Resourcefull") /* and the template's own */)
    .WithTracing(tracing => tracing.AddSource("Resourcefull") /* and the template's own */);
```

Each upload, download and other operation then shows up in the dashboard's traces, and its duration and bytes in the
metrics. To see the database calls under each operation too, add SqlClient's instrumentation
(`tracing.AddSqlClientInstrumentation()`, from the `OpenTelemetry.Instrumentation.SqlClient` package).
[Telemetry](telemetry.md) lists everything recorded.
