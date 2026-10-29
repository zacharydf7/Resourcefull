USE [ResourceDB];
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
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
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Resources]') AND [c].[name] = N'VersionId');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [Resources] DROP CONSTRAINT ' + @var + ';');
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
    DECLARE @var1 nvarchar(max);
    SELECT @var1 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Resources]') AND [c].[name] = N'Name');
    IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [Resources] DROP CONSTRAINT ' + @var1 + ';');
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
    DECLARE @var2 nvarchar(max);
    SELECT @var2 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Resources]') AND [c].[name] = N'ContainerName');
    IF @var2 IS NOT NULL EXEC(N'ALTER TABLE [Resources] DROP CONSTRAINT ' + @var2 + ';');
    ALTER TABLE [Resources] ALTER COLUMN [ContainerName] nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL;
    EXEC(N'CREATE INDEX [IX_Resources_Latest] ON [Resources] ([ContainerName], [Name]) WHERE [IsLatest] = 1');
    CREATE UNIQUE CLUSTERED INDEX [UX_Resources_Container_Name_Version] ON [Resources] ([ContainerName], [Name], [VersionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261028213132_NameLimitsAndCaseSensitiveNames'
)
BEGIN
    DECLARE @var3 nvarchar(max);
    SELECT @var3 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[ResourceContainers]') AND [c].[name] = N'Name');
    IF @var3 IS NOT NULL EXEC(N'ALTER TABLE [ResourceContainers] DROP CONSTRAINT ' + @var3 + ';');
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

