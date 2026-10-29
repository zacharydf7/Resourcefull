USE [ResourceDB];
SET XACT_ABORT ON;
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
    VALUES (N'20261029003116_EnableReadCommittedSnapshot', N'10.0.5');
END;
GO

