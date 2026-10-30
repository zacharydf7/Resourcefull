SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261029234505_AddCopiedFrom'
)
BEGIN
    ALTER TABLE [Resources] ADD [CopiedFromContainerName] nvarchar(128) COLLATE Latin1_General_100_BIN2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261029234505_AddCopiedFrom'
)
BEGIN
    ALTER TABLE [Resources] ADD [CopiedFromName] nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261029234505_AddCopiedFrom'
)
BEGIN
    ALTER TABLE [Resources] ADD [CopiedFromVersionId] nvarchar(36) COLLATE Latin1_General_100_BIN2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261029234505_AddCopiedFrom'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261029234505_AddCopiedFrom', N'10.0.14');
END;

COMMIT;
GO

