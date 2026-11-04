SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261103231332_Resourcefull_AddRetention'
)
BEGIN
    ALTER TABLE [Resources] ADD [LegalHold] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261103231332_Resourcefull_AddRetention'
)
BEGIN
    ALTER TABLE [Resources] ADD [RetainUntil] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261103231332_Resourcefull_AddRetention'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Resources_LegalHold] ON [Resources] ([ContainerName]) WHERE [LegalHold] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261103231332_Resourcefull_AddRetention'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Resources_RetainUntil] ON [Resources] ([ContainerName], [RetainUntil]) WHERE [RetainUntil] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261103231332_Resourcefull_AddRetention'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261103231332_Resourcefull_AddRetention', N'10.0.14');
END;

COMMIT;
GO

