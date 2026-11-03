SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261103061336_Resourcefull_AddDeclaredLength'
)
BEGIN
    ALTER TABLE [Resources] ADD [DeclaredLength] bigint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261103061336_Resourcefull_AddDeclaredLength'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261103061336_Resourcefull_AddDeclaredLength', N'10.0.14');
END;

COMMIT;
GO

