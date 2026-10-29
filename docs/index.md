# Resourcefull documentation

Resourcefull stores files of any type and size, called **resources**, in named **containers**, on storage you run
yourself. One client API uploads, downloads, lists, versions and deletes them, whichever storage provider holds the
bytes: SQL Server, process memory for tests, or a provider you write.

## Start here

- [Getting started](getting-started.md): install a package, register a provider, and upload and download your first
  file, in an ASP.NET Core app or a console app.

## Concepts

- [Containers and names](containers-and-names.md): how resources are organized, and the rules names follow.
- [Uploads and downloads](uploads-and-downloads.md): streams, content length and hashes, large files and
  cancellation.
- [Versioning](versioning.md): keeping older versions, restoring them, and what deleting removes.
- [Concurrency](concurrency.md): what's guaranteed when several callers write the same name.
- [Errors](errors.md): what each operation returns or throws.
- [URI providers](uri-providers.md): giving containers and resources the URIs your API serves them at.

## Providers

- [SQL Server](providers/sql-server.md): provisioning, connection strings, permissions, upgrades and
  recording hashes for older content.
- [In-memory](providers/in-memory.md): for tests and local development.
- [Writing a provider](providers/writing-a-provider.md): storing resources anywhere else.

## Guides

- [ASP.NET Core recipes](recipes/aspnet-core.md): serving downloads, accepting uploads, large files, and turning
  exceptions into status codes.
- [Upgrading](upgrading.md): moving to 3.0 from earlier releases, and the [changelog](../CHANGELOG.md).
- [Troubleshooting](troubleshooting.md): common errors and what they mean.
- [API reference](../api/index.md): every public type and member, generated from the XML documentation.

Contributing to Resourcefull itself? See [CONTRIBUTING.md](../CONTRIBUTING.md).
