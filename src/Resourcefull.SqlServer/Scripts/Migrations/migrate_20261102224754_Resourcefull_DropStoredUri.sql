SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261102224754_Resourcefull_DropStoredUri'
)
BEGIN
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Resources]') AND [c].[name] = N'Uri');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [Resources] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [Resources] DROP COLUMN [Uri];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261102224754_Resourcefull_DropStoredUri'
)
BEGIN
    DECLARE @var1 nvarchar(max);
    SELECT @var1 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[ResourceContainers]') AND [c].[name] = N'Uri');
    IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [ResourceContainers] DROP CONSTRAINT ' + @var1 + ';');
    ALTER TABLE [ResourceContainers] DROP COLUMN [Uri];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261102224754_Resourcefull_DropStoredUri'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261102224754_Resourcefull_DropStoredUri', N'10.0.14');
END;

COMMIT;
GO

