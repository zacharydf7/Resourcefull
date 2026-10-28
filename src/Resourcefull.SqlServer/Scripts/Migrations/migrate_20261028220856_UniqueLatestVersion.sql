USE [ResourceDB];
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028220856_UniqueLatestVersion'
)
BEGIN
    DROP INDEX [IX_Resources_Latest] ON [Resources];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028220856_UniqueLatestVersion'
)
BEGIN
    UPDATE r
    SET r.[IsLatest] = 0
    FROM [Resources] r
    WHERE r.[IsLatest] = 1
      AND EXISTS (
          SELECT 1 FROM [Resources] newer
          WHERE newer.[ContainerName] = r.[ContainerName]
            AND newer.[Name] = r.[Name]
            AND newer.[IsLatest] = 1
            AND newer.[Id] > r.[Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028220856_UniqueLatestVersion'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Resources_Latest] ON [Resources] ([ContainerName], [Name]) WHERE [IsLatest] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028220856_UniqueLatestVersion'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261028220856_UniqueLatestVersion', N'10.0.5');
END;

COMMIT;
GO

