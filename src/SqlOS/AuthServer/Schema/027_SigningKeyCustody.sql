IF NOT EXISTS (
    SELECT * FROM sys.tables
    WHERE name = 'SqlOSSigningKeys'
      AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSSigningKeys] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [Kid] NVARCHAR(120) NOT NULL UNIQUE,
        [Algorithm] NVARCHAR(20) NOT NULL,
        [PublicKeyPem] NVARCHAR(MAX) NOT NULL,
        [CustodyProvider] NVARCHAR(120) NOT NULL,
        [KeyReference] NVARCHAR(MAX) NOT NULL,
        [IsActive] BIT NOT NULL,
        [ActivatedAt] DATETIME2 NOT NULL,
        [RetiredAt] DATETIME2 NULL
    );
END

IF COL_LENGTH('{Schema}.SqlOSSigningKeys', 'KeyReference') IS NULL
BEGIN
    IF COL_LENGTH('{Schema}.SqlOSSigningKeys', 'PrivateKeyPem') IS NOT NULL
    BEGIN
        EXEC sp_rename
            N'[{Schema}].[SqlOSSigningKeys].[PrivateKeyPem]',
            N'KeyReference',
            'COLUMN';
    END
    ELSE
    BEGIN
        ALTER TABLE [{Schema}].[SqlOSSigningKeys]
        ADD [KeyReference] NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_SqlOSSigningKeys_KeyReference] DEFAULT '';
    END
END

IF COL_LENGTH('{Schema}.SqlOSSigningKeys', 'CustodyProvider') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSSigningKeys]
    ADD [CustodyProvider] NVARCHAR(120) NOT NULL
        CONSTRAINT [DF_SqlOSSigningKeys_CustodyProvider] DEFAULT 'legacy-unprotected';
END

IF NOT EXISTS (
    SELECT * FROM sys.indexes
    WHERE name = 'UX_SqlOSSigningKeys_OneActive'
      AND object_id = OBJECT_ID('[{Schema}].[SqlOSSigningKeys]'))
BEGIN
    CREATE UNIQUE INDEX [UX_SqlOSSigningKeys_OneActive]
    ON [{Schema}].[SqlOSSigningKeys] ([IsActive])
    WHERE [IsActive] = 1;
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (27);
