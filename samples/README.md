# Samples

Three apps that use Resourcefull from source. Each runs out of the box, keeping resources in memory.

| Sample | What it shows |
|---|---|
| [`FileService`](https://github.com/zacharydf7/Resourcefull/tree/main/samples/FileService) | A file service shaped like a production deployment: each signed-in user has a container of their own with a quota and a list of accepted file types, downloads support ranges and caching, files can be shared through a link that expires, and the service reports its health and its OpenTelemetry traces and metrics. Start here for a real application. |
| [`TestApiClient`](https://github.com/zacharydf7/Resourcefull/tree/main/samples/TestApiClient) | A minimal API over containers and resources: create, list, upload (a form file, or a request body with `If-Match`), download (the info, or the content with ETags and conditional requests), delete and restore, with the `Resourcefull.AspNetCore` package. Open `/swagger`, or send the requests in `TestApiClient.http`. |
| [`BlazorTestClient`](https://github.com/zacharydf7/Resourcefull/tree/main/samples/BlazorTestClient) | A Blazor Server page for managing containers and resources, with version history when versioning is on. |

```shell
dotnet run --project samples/FileService
dotnet run --project samples/TestApiClient
dotnet run --project samples/BlazorTestClient
```

## The file service

`FileService` answers only signed-in users, who send a bearer token. In development, make one and send it as
`Authorization: Bearer <token>`, or paste it into `FileService.http`, which has a request for each endpoint:

```shell
dotnet user-jwts create --project samples/FileService --name alice -o token
```

| Endpoint | What it does |
|---|---|
| `PUT /files/{name}` | Uploads the request body as the file, typed by its extension rather than by the request's `Content-Type`. `If-None-Match: *` refuses to replace one, and `If-Match` replaces only the version you have. The user's container is created on their first upload. |
| `GET /files/{name}` | Downloads the file, with `ETag`, `Last-Modified`, conditional requests and `Range` requests. |
| `GET /files/` | Lists the user's files, 100 at a time; pass the `next` value it returns as `?after=` for the next page. |
| `GET /usage` | How much of the user's quota their files take. |
| `POST /files/share/{name}` | Returns a link to the file that anyone can open, without signing in, for a day. |
| `DELETE /files/{name}` | Deletes the file. |
| `GET /health/live`, `GET /health/ready` | Whether the process answers, and whether storage does too. |

The quota and the accepted file types come from `Files:DefaultPolicy` in `appsettings.json`. A refused upload gets
413 for a file too large or a full quota, and 415 for a type the service doesn't accept or an extension it doesn't
know. Every
operation's traces and metrics go to the OpenTelemetry collector that `OTEL_EXPORTER_OTLP_ENDPOINT` names, such as the
.NET Aspire dashboard's.

Before running it as a real service:

- Give it SQL Server, below.
- Persist its Data Protection keys where every instance reads them, since they sign the shared links: see
  [Sharing links in production](../docs/sharing-links-in-production.md).
- Behind a proxy that ends TLS, turn on ASP.NET Core's forwarded headers, so the shared links it builds use the
  address clients see rather than its own.

## Storing resources in SQL Server

Set the `Resources` connection string and the sample uses SQL Server instead. At startup it calls
`MigrateResourcefullDatabaseAsync`, which creates the database if it doesn't exist and brings its schema up to
date, so there's no script to run first. Use a database of its own: provisioning turns on `READ_COMMITTED_SNAPSHOT`
for the whole database.

Keep the connection string out of source control with user secrets or an environment variable:

```shell
dotnet user-secrets --project samples/TestApiClient set ConnectionStrings:Resources "Server=(localdb)\MSSQLLocalDB;Database=ResourcefullSamples;Integrated Security=True;TrustServerCertificate=True"
```

```shell
export ConnectionStrings__Resources="Server=localhost,1433;Database=ResourcefullSamples;User ID=sa;Password=<your password>;TrustServerCertificate=True"
```

LocalDB, in the first example, comes with Visual Studio on Windows. Elsewhere, point it at any SQL Server, such as the
`mcr.microsoft.com/mssql/server` container.

## Versioning

Versioning is off by default. Uncomment `options.UseVersioning();` in `TestApiClient`'s or `BlazorTestClient`'s
`Program.cs` to keep older versions of each resource, which the Blazor page then lists and can restore.

## Resource URIs

`TestApiClient` and `BlazorTestClient` register `TestUriProvider`, which gives each container and resource a URI
under the `BaseUrl` in `appsettings.json`, pointing at `TestApiClient`. The Blazor sample's links open only while
`TestApiClient` is running and both samples use the same SQL Server database: in memory, each sample has a store of
its own.
