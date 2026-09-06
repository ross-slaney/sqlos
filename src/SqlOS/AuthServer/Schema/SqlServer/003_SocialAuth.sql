IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSAuthSocialConnections' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSAuthSocialConnections] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [ProviderType] NVARCHAR(40) NOT NULL,
        [DisplayName] NVARCHAR(200) NOT NULL,
        [ClientId] NVARCHAR(300) NOT NULL,
        [ClientSecretEncrypted] NVARCHAR(MAX) NOT NULL,
        [AllowedCallbackUrisJson] NVARCHAR(MAX) NOT NULL,
        [MicrosoftTenant] NVARCHAR(200) NULL,
        [ScopesJson] NVARCHAR(MAX) NOT NULL,
        [IsEnabled] BIT NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL
    );
END

GO

IF COL_LENGTH('{Schema}.SqlOSExternalIdentities', 'SocialConnectionId') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSExternalIdentities]
    ADD [SocialConnectionId] NVARCHAR(64) NULL;
END

GO

IF EXISTS (
    SELECT 1
    FROM sys.key_constraints
    WHERE [name] = 'UQ_SqlOSExternalIdentities_Connection_Subject'
      AND [parent_object_id] = OBJECT_ID('[{Schema}].[SqlOSExternalIdentities]')
)
BEGIN
    ALTER TABLE [{Schema}].[SqlOSExternalIdentities]
    DROP CONSTRAINT [UQ_SqlOSExternalIdentities_Connection_Subject];
END

IF EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE [name] = 'IX_SqlOSExternalIdentities_ConnectionId_Subject'
      AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSExternalIdentities]')
)
BEGIN
    DROP INDEX [IX_SqlOSExternalIdentities_ConnectionId_Subject]
    ON [{Schema}].[SqlOSExternalIdentities];
END

GO

IF EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE [name] = 'FK_SqlOSExternalIdentities_SsoConnections'
      AND [parent_object_id] = OBJECT_ID('[{Schema}].[SqlOSExternalIdentities]')
)
BEGIN
    ALTER TABLE [{Schema}].[SqlOSExternalIdentities]
    DROP CONSTRAINT [FK_SqlOSExternalIdentities_SsoConnections];
END

GO

IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID('[{Schema}].[SqlOSExternalIdentities]')
      AND [name] = 'ConnectionId'
      AND [is_nullable] = 0
)
BEGIN
    ALTER TABLE [{Schema}].[SqlOSExternalIdentities]
    ALTER COLUMN [ConnectionId] NVARCHAR(64) NULL;
END

GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE [name] = 'FK_SqlOSExternalIdentities_SsoConnections'
      AND [parent_object_id] = OBJECT_ID('[{Schema}].[SqlOSExternalIdentities]')
)
BEGIN
    ALTER TABLE [{Schema}].[SqlOSExternalIdentities]
    ADD CONSTRAINT [FK_SqlOSExternalIdentities_SsoConnections]
        FOREIGN KEY ([ConnectionId]) REFERENCES [{Schema}].[SqlOSSsoConnections]([Id]);
END

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE [name] = 'FK_SqlOSExternalIdentities_SocialConnections'
      AND [parent_object_id] = OBJECT_ID('[{Schema}].[SqlOSExternalIdentities]')
)
BEGIN
    ALTER TABLE [{Schema}].[SqlOSExternalIdentities]
    ADD CONSTRAINT [FK_SqlOSExternalIdentities_SocialConnections]
        FOREIGN KEY ([SocialConnectionId]) REFERENCES [{Schema}].[SqlOSAuthSocialConnections]([Id]);
END

GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE [name] = 'IX_SqlOSExternalIdentities_SsoConnectionId_Subject'
      AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSExternalIdentities]')
)
BEGIN
    CREATE UNIQUE INDEX [IX_SqlOSExternalIdentities_SsoConnectionId_Subject]
    ON [{Schema}].[SqlOSExternalIdentities]([ConnectionId], [Subject])
    WHERE [ConnectionId] IS NOT NULL;
END

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE [name] = 'IX_SqlOSExternalIdentities_SocialConnectionId_Subject'
      AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSExternalIdentities]')
)
BEGIN
    CREATE UNIQUE INDEX [IX_SqlOSExternalIdentities_SocialConnectionId_Subject]
    ON [{Schema}].[SqlOSExternalIdentities]([SocialConnectionId], [Subject])
    WHERE [SocialConnectionId] IS NOT NULL;
END

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (3);
