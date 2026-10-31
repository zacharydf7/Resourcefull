SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031020447_Resourcefull_AddStagedAt'
)
BEGIN
    ALTER TABLE [Resources] ADD [StagedAt] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031020447_Resourcefull_AddStagedAt'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Resources_StagedAt] ON [Resources] ([StagedAt]) INCLUDE ([Id]) WHERE [StagedAt] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031020447_Resourcefull_AddStagedAt'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261031020447_Resourcefull_AddStagedAt', N'10.0.14');
END;

COMMIT;
GO

