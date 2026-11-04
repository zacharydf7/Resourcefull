SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104022754_Resourcefull_AddBin'
)
BEGIN
    ALTER TABLE [Resources] ADD [DeletedAt] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104022754_Resourcefull_AddBin'
)
BEGIN
    ALTER TABLE [Resources] ADD [PurgeAt] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104022754_Resourcefull_AddBin'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Resources_DeletedAt] ON [Resources] ([ContainerName], [DeletedAt] DESC, [VersionId]) WHERE [DeletedAt] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104022754_Resourcefull_AddBin'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Resources_PurgeAt] ON [Resources] ([PurgeAt], [VersionId]) WHERE [PurgeAt] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104022754_Resourcefull_AddBin'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261104022754_Resourcefull_AddBin', N'10.0.14');
END;

COMMIT;
GO

