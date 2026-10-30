SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240611171637_InitialCreate'
)
BEGIN
    CREATE TABLE [ResourceContainers] (
        [Name] nvarchar(450) NOT NULL,
        [Uri] nvarchar(max) NULL,
        [Metadata] varchar(MAX) NOT NULL,
        CONSTRAINT [PK_ResourceContainers] PRIMARY KEY ([Name])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240611171637_InitialCreate'
)
BEGIN
    CREATE TABLE [Resources] (
        [ContainerName] nvarchar(450) NOT NULL,
        [Name] nvarchar(450) NOT NULL,
        [ContentType] nvarchar(max) NULL,
        [ContentLength] bigint NULL,
        [Uri] nvarchar(max) NULL,
        [Metadata] varchar(MAX) NOT NULL,
        CONSTRAINT [PK_Resources] PRIMARY KEY ([Name], [ContainerName]),
        CONSTRAINT [FK_Resources_ResourceContainers_ContainerName] FOREIGN KEY ([ContainerName]) REFERENCES [ResourceContainers] ([Name]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240611171637_InitialCreate'
)
BEGIN
    CREATE TABLE [ResourceChunks] (
        [ResourceContainerName] nvarchar(450) NOT NULL,
        [ResourceName] nvarchar(450) NOT NULL,
        [Index] int NOT NULL,
        [Data] varbinary(max) NOT NULL,
        CONSTRAINT [PK_ResourceChunks] PRIMARY KEY ([ResourceName], [ResourceContainerName], [Index]),
        CONSTRAINT [FK_ResourceChunks_Resources_ResourceName_ResourceContainerName] FOREIGN KEY ([ResourceName], [ResourceContainerName]) REFERENCES [Resources] ([Name], [ContainerName]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240611171637_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Resources_ContainerName] ON [Resources] ([ContainerName]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20240611171637_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20240611171637_InitialCreate', N'10.0.14');
END;

COMMIT;
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
    VALUES (N'20250513172319_AddVersioning', N'10.0.14');
END;

COMMIT;
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

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028213132_NameLimitsAndCaseSensitiveNames'
)
BEGIN
    UPDATE r SET [ContainerName] = c.[Name]
    FROM [Resources] r
    JOIN [ResourceContainers] c ON c.[Name] = r.[ContainerName]
    WHERE CAST(r.[ContainerName] AS varbinary(900)) <> CAST(c.[Name] AS varbinary(900));

    UPDATE r SET [Name] = latest.[Name]
    FROM [Resources] r
    CROSS APPLY (
        SELECT TOP (1) l.[Name] FROM [Resources] l
        WHERE l.[ContainerName] = r.[ContainerName] AND l.[Name] = r.[Name]
        ORDER BY l.[IsLatest] DESC, l.[Id] DESC) latest
    WHERE CAST(r.[Name] AS varbinary(900)) <> CAST(latest.[Name] AS varbinary(900));
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028213132_NameLimitsAndCaseSensitiveNames'
)
BEGIN
    ALTER TABLE [Resources] DROP CONSTRAINT [FK_Resources_ResourceContainers_ContainerName];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028213132_NameLimitsAndCaseSensitiveNames'
)
BEGIN
    ALTER TABLE [ResourceContainers] DROP CONSTRAINT [PK_ResourceContainers];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028213132_NameLimitsAndCaseSensitiveNames'
)
BEGIN
    DROP INDEX [UX_Resources_Container_Name_Version] ON [Resources];
    DECLARE @var3 nvarchar(max);
    SELECT @var3 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Resources]') AND [c].[name] = N'VersionId');
    IF @var3 IS NOT NULL EXEC(N'ALTER TABLE [Resources] DROP CONSTRAINT ' + @var3 + ';');
    ALTER TABLE [Resources] ALTER COLUMN [VersionId] nvarchar(36) COLLATE Latin1_General_100_BIN2 NOT NULL;
    CREATE UNIQUE CLUSTERED INDEX [UX_Resources_Container_Name_Version] ON [Resources] ([ContainerName], [Name], [VersionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028213132_NameLimitsAndCaseSensitiveNames'
)
BEGIN
    DROP INDEX [IX_Resources_Latest] ON [Resources];
    DROP INDEX [UX_Resources_Container_Name_Version] ON [Resources];
    DECLARE @var4 nvarchar(max);
    SELECT @var4 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Resources]') AND [c].[name] = N'Name');
    IF @var4 IS NOT NULL EXEC(N'ALTER TABLE [Resources] DROP CONSTRAINT ' + @var4 + ';');
    ALTER TABLE [Resources] ALTER COLUMN [Name] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL;
    EXEC(N'CREATE INDEX [IX_Resources_Latest] ON [Resources] ([ContainerName], [Name]) WHERE [IsLatest] = 1');
    CREATE UNIQUE CLUSTERED INDEX [UX_Resources_Container_Name_Version] ON [Resources] ([ContainerName], [Name], [VersionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028213132_NameLimitsAndCaseSensitiveNames'
)
BEGIN
    DROP INDEX [IX_Resources_Latest] ON [Resources];
    DROP INDEX [UX_Resources_Container_Name_Version] ON [Resources];
    DECLARE @var5 nvarchar(max);
    SELECT @var5 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Resources]') AND [c].[name] = N'ContainerName');
    IF @var5 IS NOT NULL EXEC(N'ALTER TABLE [Resources] DROP CONSTRAINT ' + @var5 + ';');
    ALTER TABLE [Resources] ALTER COLUMN [ContainerName] nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL;
    EXEC(N'CREATE INDEX [IX_Resources_Latest] ON [Resources] ([ContainerName], [Name]) WHERE [IsLatest] = 1');
    CREATE UNIQUE CLUSTERED INDEX [UX_Resources_Container_Name_Version] ON [Resources] ([ContainerName], [Name], [VersionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028213132_NameLimitsAndCaseSensitiveNames'
)
BEGIN
    DECLARE @var6 nvarchar(max);
    SELECT @var6 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[ResourceContainers]') AND [c].[name] = N'Name');
    IF @var6 IS NOT NULL EXEC(N'ALTER TABLE [ResourceContainers] DROP CONSTRAINT ' + @var6 + ';');
    ALTER TABLE [ResourceContainers] ALTER COLUMN [Name] nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028213132_NameLimitsAndCaseSensitiveNames'
)
BEGIN
    ALTER TABLE [ResourceContainers] ADD CONSTRAINT [PK_ResourceContainers] PRIMARY KEY ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028213132_NameLimitsAndCaseSensitiveNames'
)
BEGIN
    ALTER TABLE [Resources] ADD CONSTRAINT [FK_Resources_ResourceContainers_ContainerName] FOREIGN KEY ([ContainerName]) REFERENCES [ResourceContainers] ([Name]) ON DELETE CASCADE;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028213132_NameLimitsAndCaseSensitiveNames'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261028213132_NameLimitsAndCaseSensitiveNames', N'10.0.14');
END;

COMMIT;
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
    VALUES (N'20261028220856_UniqueLatestVersion', N'10.0.14');
END;

COMMIT;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261029003116_EnableReadCommittedSnapshot'
)
BEGIN
    IF (SELECT [is_read_committed_snapshot_on] FROM sys.databases WHERE [database_id] = DB_ID()) = 0
        ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261029003116_EnableReadCommittedSnapshot'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261029003116_EnableReadCommittedSnapshot', N'10.0.14');
END;
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

