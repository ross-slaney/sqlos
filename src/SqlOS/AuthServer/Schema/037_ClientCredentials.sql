IF OBJECT_ID('[{Schema}].[SqlOSClientApplications]', 'U') IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM sys.tables
    WHERE [name] = 'SqlOSClientCredentials'
      AND [schema_id] = SCHEMA_ID('{Schema}')
)
BEGIN
    CREATE TABLE [{Schema}].[SqlOSClientCredentials] (
        [Id] NVARCHAR(64) NOT NULL,
        [ClientApplicationId] NVARCHAR(64) NOT NULL,
        [SecretHash] NVARCHAR(MAX) NOT NULL,
        [DisplayName] NVARCHAR(200) NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [ExpiresAt] DATETIME2 NULL,
        [RevokedAt] DATETIME2 NULL,
        [LastUsedAt] DATETIME2 NULL,
        [ConfigurationOwner] NVARCHAR(40) NOT NULL
            CONSTRAINT [DF_SqlOSClientCredentials_ConfigurationOwner] DEFAULT 'dashboard',
        [ConfigurationSourceKey] NVARCHAR(160) NULL,
        [LastReconciledAt] DATETIME2 NULL,
        CONSTRAINT [PK_SqlOSClientCredentials] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SqlOSClientCredentials_ClientApplications]
            FOREIGN KEY ([ClientApplicationId])
            REFERENCES [{Schema}].[SqlOSClientApplications]([Id])
    );
END

GO

IF OBJECT_ID('[{Schema}].[SqlOSClientCredentials]', 'U') IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE [name] = 'IX_SqlOSClientCredentials_Active'
      AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSClientCredentials]')
)
BEGIN
    CREATE INDEX [IX_SqlOSClientCredentials_Active]
        ON [{Schema}].[SqlOSClientCredentials] ([ClientApplicationId], [RevokedAt], [ExpiresAt]);
END

GO

IF OBJECT_ID('[{Schema}].[SqlOSClientCredentials]', 'U') IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE [name] = 'UX_SqlOSClientCredentials_Client_Owner_SourceKey'
      AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSClientCredentials]')
)
BEGIN
    CREATE UNIQUE INDEX [UX_SqlOSClientCredentials_Client_Owner_SourceKey]
        ON [{Schema}].[SqlOSClientCredentials] (
            [ClientApplicationId],
            [ConfigurationOwner],
            [ConfigurationSourceKey]
        )
        WHERE [ConfigurationSourceKey] IS NOT NULL;
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (37);
