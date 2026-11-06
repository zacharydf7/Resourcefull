SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261106153821_Resourcefull_SweepKeys'
)
BEGIN
    DROP INDEX [IX_Resources_DeletedAt] ON [Resources];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261106153821_Resourcefull_SweepKeys'
)
BEGIN
    DROP INDEX [IX_Resources_ExpiresAt] ON [Resources];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261106153821_Resourcefull_SweepKeys'
)
BEGIN
    DROP INDEX [IX_Resources_PurgeAt] ON [Resources];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261106153821_Resourcefull_SweepKeys'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Resources_DeletedAt] ON [Resources] ([ContainerName], [DeletedAt] DESC, [VersionId], [Name]) WHERE [DeletedAt] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261106153821_Resourcefull_SweepKeys'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Resources_ExpiresAt] ON [Resources] ([ExpiresAt], [VersionId], [ContainerName], [Name]) WHERE [ExpiresAt] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261106153821_Resourcefull_SweepKeys'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Resources_PurgeAt] ON [Resources] ([PurgeAt], [VersionId], [ContainerName], [Name]) WHERE [PurgeAt] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261106153821_Resourcefull_SweepKeys'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261106153821_Resourcefull_SweepKeys', N'10.0.14');
END;

COMMIT;
GO

