# The dashboard

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

`Resourcefull.AspNetCore` includes a small dashboard: a page in your application that shows what's stored. Map it
with one line, much as you'd map Swagger UI, and it shows:

- **Health:** the result of Resourcefull's [health check](../health-checks.md), including migrations the database is
  missing, or has from a newer release.
- **Containers:** each one's stored size against its `MaxContainerSize`, its files against `MaxResourceCount`, how
  many versions it holds, and the rest of its [policy](../container-policies.md): whether it's read-only, the types it
  allows, the largest file it takes and how many versions it keeps.
- **One container:** its metadata and limits, and its files, with each one's size, type, last change and expiry.

The dashboard only reads: it can't delete, change or download anything.

## Setting it up

```csharp
using Resourcefull.AspNetCore;

builder.Services.AddHealthChecks().AddResourcefullSqlServerCheck();

app.UseAuthentication();
app.UseAuthorization();
app.MapResourcefullDashboard("/resourcefull").RequireAuthorization("admins");
```

Then open `/resourcefull` in a browser. `admins` is an authorization policy you define with `AddAuthorization`, as in
[Securing file endpoints](securing-endpoints.md). Registering the health check is optional; without one, the page
says none is registered.

### Who may see it

The dashboard shows container names, file names and metadata, which can be personal data. So it doesn't run until
you've decided who may see it: until then, every request to it throws `InvalidOperationException`, saying what to add.
Any one of these counts as a decision:

- `RequireAuthorization` on the group `MapResourcefullDashboard` returns, with or without a policy.
- A fallback authorization policy (`AuthorizationOptions.FallbackPolicy`) that covers every endpoint without its own.
- `AllowAnonymous()` on the group, which shows it to anyone who can reach it. Use that only for a tool that runs
  on your own machine, never on a server.

Because `MapResourcefullDashboard` returns the route group, anything else you'd put on endpoints applies to it too,
such as `RequireHost("admin.example.com")` or rate limiting.

### Health checks

The dashboard runs the health checks named `resourcefull`, the name Resourcefull's checks get unless you give them
another. If you registered yours under other names, say which:

```csharp
app.MapResourcefullDashboard("/resourcefull", new ResourcefullDashboardOptions
{
    HealthChecks = ["files-db", "archive-db"]
}).RequireAuthorization("admins");
```

## What it costs

Each page of containers shows up to 50, and each one's usage is a query of its own (policies are cached), so a page
is up to 50 small queries on top of the listing. Usage adds up the container's versions, so the more versions a
container holds, the longer its query takes. The dashboard is built for an administrator looking now and then, not
for a wall display refreshing every few seconds.

## The JSON endpoints

The page reads four endpoints under `{pattern}/api/`, which you can call yourself, as a small admin API, to show the
same data your own way. They answer JSON with camelCase names whatever your application's JSON settings are, and
follow the same authorization as the page.

| Endpoint | Returns |
|---|---|
| `GET api/health` | `status` (`Healthy`, `Degraded` or `Unhealthy`, or `null` when no check is registered) and `checks`: each one's `name`, `status`, `description` and `data`. |
| `GET api/containers?prefix=&continuationToken=` | `items`: up to 50 containers, and `continuationToken` for the next page, or `null`. |
| `GET api/container?name=` | One container, or 404 when it doesn't exist. |
| `GET api/resources?container=&prefix=&continuationToken=` | `items`: up to 50 of the container's current files, and `continuationToken`; 404 when the container doesn't exist. |

A container holds `name`, `metadata`, `usage` and `policy`. `usage` holds `storedSize` in bytes, `resourceCount` and
`versionCount`. `policy` holds `maxResourceSize` and `maxContainerSize` in bytes, `maxResourceCount` and
`allowedContentTypes`, each `null` when there's no limit, plus `readOnly` and `maxVersions`, the versions kept per
name. A file holds `name`, `versionId`, `contentType`, `contentLength`, `lastModified` and `expiresAt`.

A malformed continuation token, a token from a listing with a different `prefix`, or a container name the
[name rules](../names.md) refuse gets 400 Bad Request.

The container's name goes in the query string rather than the path, because container names can hold `/`.

## The page

The page is three files, `{pattern}/`, `dashboard.js` and `dashboard.css`, served from the package. It loads nothing
from other sites and has no inline script, so a Content Security Policy of `default-src 'self'` doesn't block it.
Opening `{pattern}` without the slash redirects to it, since the page finds its files relative to that.

## Telemetry

Each request to the dashboard gets `resourcefull.dashboard` on its span, naming what it asked for: `page`, `health`,
`containers`, `container` or `resources`. The reads behind it record their usual operations, such as
`list_containers`, `get_usage` and `list_page`. For activity over time (uploads, downloads, errors), use the
[metrics](../telemetry.md) rather than the dashboard, which only shows the current state.
