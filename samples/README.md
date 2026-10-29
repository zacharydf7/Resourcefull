# Samples

Two small apps that use Resourcefull from source. Both run out of the box, keeping resources in memory.

| Sample | What it shows |
|---|---|
| `TestApiClient` | A minimal API over containers and resources: create, list, upload (a form file, or a request body with `If-Match`), download (the info, or the content with ETags and conditional requests), delete and restore, with the `Resourcefull.AspNetCore` package. Open `/swagger`, or send the requests in `TestApiClient.http`. |
| `BlazorTestClient` | A Blazor Server page for managing containers and resources, with version history when versioning is on. |

```shell
dotnet run --project samples/TestApiClient
dotnet run --project samples/BlazorTestClient
```

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

Versioning is off by default. Uncomment `options.UseVersioning();` in a sample's `Program.cs` to keep older versions
of each resource, which the Blazor page then lists and can restore.

## Resource URIs

Both samples register `TestUriProvider`, which gives each container and resource a URI under the `BaseUrl` in
`appsettings.json`, pointing at `TestApiClient`. The Blazor sample's links open only while `TestApiClient` is running
and both samples use the same SQL Server database: in memory, each sample has a store of its own.
