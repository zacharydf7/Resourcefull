USE [ResourceDB];
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250513172319_AddVersioning'
)
BEGIN
    ALTER TABLE [ResourceChunks] DROP CONSTRAINT [FK_ResourceChunks_Resources_ResourceName_ResourceContainerName];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250513172319_AddVersioning'
)
BEGIN
    ALTER TABLE [Resources] DROP CONSTRAINT [PK_Resources];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250513172319_AddVersioning'
)
BEGIN
    ALTER TABLE [ResourceChunks] DROP CONSTRAINT [PK_ResourceChunks];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250513172319_AddVersioning'
)
BEGIN
    ALTER TABLE [Resources] ADD [VersionId] nvarchar(450) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250513172319_AddVersioning'
)
BEGIN
    ALTER TABLE [Resources] ADD [LastModified] datetimeoffset NOT NULL DEFAULT '0001-01-01T00:00:00.0000000+00:00';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250513172319_AddVersioning'
)
BEGIN
    ALTER TABLE [ResourceChunks] ADD [ResourceVersionId] nvarchar(450) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250513172319_AddVersioning'
)
BEGIN
    EXEC(N'
    UPDATE [Resources]
    SET [VersionId] = FORMAT(GETUTCDATE(), ''yyyy-MM-dd''''T''''HH:mm:ss.fffffff''''Z''''''),
        [LastModified] = GETUTCDATE()
    WHERE [VersionId] = '''';
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250513172319_AddVersioning'
)
BEGIN
    EXEC(N'
    UPDATE [ResourceChunks]
    SET [ResourceVersionId] = (SELECT [VersionId] FROM [Resources] WHERE [Resources].[Name] = [ResourceChunks].[ResourceName] AND [Resources].[ContainerName] = [ResourceChunks].[ResourceContainerName])
    WHERE [ResourceVersionId] = '''';
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250513172319_AddVersioning'
)
BEGIN
    ALTER TABLE [Resources] ADD CONSTRAINT [PK_Resources] PRIMARY KEY ([Name], [ContainerName], [VersionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250513172319_AddVersioning'
)
BEGIN
    ALTER TABLE [ResourceChunks] ADD CONSTRAINT [PK_ResourceChunks] PRIMARY KEY ([ResourceName], [ResourceContainerName], [ResourceVersionId], [Index]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250513172319_AddVersioning'
)
BEGIN
    ALTER TABLE [ResourceChunks] ADD CONSTRAINT [FK_ResourceChunks_Resources_ResourceName_ResourceContainerName_ResourceVersionId] FOREIGN KEY ([ResourceName], [ResourceContainerName], [ResourceVersionId]) REFERENCES [Resources] ([Name], [ContainerName], [VersionId]) ON DELETE CASCADE;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20250513172319_AddVersioning'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20250513172319_AddVersioning', N'10.0.5');
END;

COMMIT;
GO

