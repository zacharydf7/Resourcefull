SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030011747_AddExpiresAt'
)
BEGIN
    ALTER TABLE [Resources] ADD [ExpiresAt] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030011747_AddExpiresAt'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Resources_ExpiresAt] ON [Resources] ([ExpiresAt], [VersionId]) WHERE [ExpiresAt] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030011747_AddExpiresAt'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261030011747_AddExpiresAt', N'10.0.14');
END;

COMMIT;
GO

