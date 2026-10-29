USE [ResourceDB];
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261029021712_AddContentHash'
)
BEGIN
    ALTER TABLE [Resources] ADD [ContentHash] varchar(71) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261029021712_AddContentHash'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261029021712_AddContentHash', N'10.0.14');
END;

COMMIT;
GO

