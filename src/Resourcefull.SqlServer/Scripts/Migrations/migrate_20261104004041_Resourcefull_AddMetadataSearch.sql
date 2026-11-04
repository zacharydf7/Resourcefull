SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104004041_Resourcefull_AddMetadataSearch'
)
BEGIN
    CREATE TABLE [ResourceMetadata] (
        [ResourceId] bigint NOT NULL,
        [Key] nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [ContainerName] nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [Name] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [Value] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
        CONSTRAINT [PK_ResourceMetadata] PRIMARY KEY ([ResourceId], [Key]),
        CONSTRAINT [FK_ResourceMetadata_Resources_ResourceId] FOREIGN KEY ([ResourceId]) REFERENCES [Resources] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104004041_Resourcefull_AddMetadataSearch'
)
BEGIN
    CREATE INDEX [IX_ResourceMetadata_Search] ON [ResourceMetadata] ([ContainerName], [Key], [Value], [Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104004041_Resourcefull_AddMetadataSearch'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261104004041_Resourcefull_AddMetadataSearch', N'10.0.14');
END;

COMMIT;
GO

