USE [ResourceDB];
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    ALTER TABLE [Resources] ADD [Id] bigint NOT NULL IDENTITY;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    ALTER TABLE [Resources] ADD [IsLatest] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    ALTER TABLE [ResourceChunks] ADD [ResourceId] bigint NOT NULL DEFAULT CAST(0 AS bigint);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    EXEC(N'
    UPDATE c
    SET c.[ResourceId] = r.[Id]
    FROM [ResourceChunks] c
    INNER JOIN [Resources] r
        ON c.[ResourceName] = r.[Name]
       AND c.[ResourceContainerName] = r.[ContainerName]
       AND c.[ResourceVersionId] = r.[VersionId];
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    EXEC(N'
    WITH latest AS (
        SELECT [Id],
               ROW_NUMBER() OVER (
                   PARTITION BY [ContainerName], [Name]
                   ORDER BY [LastModified] DESC, [Id] DESC) AS rn
        FROM [Resources]
    )
    UPDATE r
    SET r.[IsLatest] = 1
    FROM [Resources] r
    INNER JOIN latest l ON r.[Id] = l.[Id]
    WHERE l.rn = 1;
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    ALTER TABLE [ResourceChunks] DROP CONSTRAINT [FK_ResourceChunks_Resources_ResourceName_ResourceContainerName_ResourceVersionId];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    ALTER TABLE [Resources] DROP CONSTRAINT [PK_Resources];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    DROP INDEX [IX_Resources_ContainerName] ON [Resources];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    ALTER TABLE [ResourceChunks] DROP CONSTRAINT [PK_ResourceChunks];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[ResourceChunks]') AND [c].[name] = N'ResourceName');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [ResourceChunks] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [ResourceChunks] DROP COLUMN [ResourceName];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    DECLARE @var1 nvarchar(max);
    SELECT @var1 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[ResourceChunks]') AND [c].[name] = N'ResourceContainerName');
    IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [ResourceChunks] DROP CONSTRAINT ' + @var1 + ';');
    ALTER TABLE [ResourceChunks] DROP COLUMN [ResourceContainerName];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    DECLARE @var2 nvarchar(max);
    SELECT @var2 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[ResourceChunks]') AND [c].[name] = N'ResourceVersionId');
    IF @var2 IS NOT NULL EXEC(N'ALTER TABLE [ResourceChunks] DROP CONSTRAINT ' + @var2 + ';');
    ALTER TABLE [ResourceChunks] DROP COLUMN [ResourceVersionId];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    ALTER TABLE [Resources] ADD CONSTRAINT [PK_Resources] PRIMARY KEY NONCLUSTERED ([Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    ALTER TABLE [ResourceChunks] ADD CONSTRAINT [PK_ResourceChunks] PRIMARY KEY ([ResourceId], [Index]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Resources_Latest] ON [Resources] ([ContainerName], [Name]) WHERE [IsLatest] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [UX_Resources_Container_Name_Version] ON [Resources] ([ContainerName], [Name], [VersionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    ALTER TABLE [ResourceChunks] ADD CONSTRAINT [FK_ResourceChunks_Resources_ResourceId] FOREIGN KEY ([ResourceId]) REFERENCES [Resources] ([Id]) ON DELETE CASCADE;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260624193539_RedesignKeysAndVersioning'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260624193539_RedesignKeysAndVersioning', N'10.0.14');
END;

COMMIT;
GO

