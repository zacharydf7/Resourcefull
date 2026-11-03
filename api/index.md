# API reference

Every public type and member of the four packages, generated from their XML documentation.

- **`Resourcefull`**: the clients you use (`IResourceServiceClient`, `IResourceContainerClient`), their models and
  exceptions, registration (`AddResourcefull`, `ResourcefullOptionsBuilder`), provisioning
  (`MigrateResourcefullDatabaseAsync`, `GenerateResourcefullScript`), and, in `Resourcefull.Providers`, what a provider
  builds on (`ResourceRepository`, `NewResourceVersion`, `ResourceLocation`, `ResourceDatabase`, `HashingStream`,
  `WrittenContent`).
- **`Resourcefull.InMemory`**: `UseInMemory`.
- **`Resourcefull.SqlServer`**: `UseSqlServer` and `ContentHashBackfill`.
- **`Resourcefull.AspNetCore`**: `Results.Resource`, `UploadFromRequestAsync`, `AddResourcefullExceptionHandler`, and
  signed links (`AddResourcefullLinks`, `IResourceLinkSigner`, `Results.SharedResource`).
