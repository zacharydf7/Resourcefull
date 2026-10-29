# API reference

Every public type and member of the three packages, generated from their XML documentation.

- **`Resourcefull`**: the clients you use (`IResourceServiceClient`, `IResourceContainerClient`), their models and
  exceptions, registration (`AddResourcefull`, `ResourcefullOptionsBuilder`), and the provider interface
  (`IResourceRepository`, `HashingStream`, `WrittenContent`).
- **`Resourcefull.InMemory`**: `UseInMemory`.
- **`Resourcefull.SqlServer`**: `UseSqlServer`, `MigrateResourcefullDatabaseAsync` and `ContentHashBackfill`.
