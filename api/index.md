# API reference

Every public type and member of the seven library packages, generated from their XML documentation.

- **`Resourcefull`**: the clients you use (`IResourceServiceClient`, `IResourceContainerClient`), their models and
  exceptions, registration (`AddResourcefull`, `ResourcefullOptionsBuilder`), provisioning
  (`MigrateResourcefullDatabaseAsync`, `GenerateResourcefullScript`), and, in `Resourcefull.Providers`, what a provider
  builds on (`ResourceRepository`, `NewResourceVersion`, `ResourceAddress`, `ResourceDatabase`, `HashingStream`,
  `WrittenContent`).
- **`Resourcefull.InMemory`**: `UseInMemory`.
- **`Resourcefull.SqlServer`**: `UseSqlServer`, `ResourcefullSqlServerOptions` and `ContentHashBackfill`.
- **`Resourcefull.PostgreSql`**: `UsePostgreSql`, `ResourcefullPostgreSqlOptions` and `AddResourcefullPostgreSqlCheck`.
- **`Resourcefull.S3`**: `UseS3` and `ResourcefullS3Options`.
- **`Resourcefull.AzureBlob`**: `UseAzureBlob` and `ResourcefullAzureBlobOptions`.
- **`Resourcefull.AspNetCore`**: `Results.Resource`, `UploadFromRequestAsync`, `AddResourcefullExceptionHandler`,
  signed download and upload links (`AddResourcefullLinks`, `IResourceLinkSigner`, `Results.SharedResource`,
  `Results.SharedUpload`), and the dashboard (`MapResourcefullDashboard`).

The eighth package, `Resourcefull.Testing`, holds the contract tests for checking a provider; see
[Testing a provider](../docs/providers/writing-a-provider.md#testing-a-provider).
