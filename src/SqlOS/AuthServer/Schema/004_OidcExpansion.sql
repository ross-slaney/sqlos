IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSAuthOidcConnections' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSAuthOidcConnections] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [ProviderType] NVARCHAR(40) NOT NULL,
        [DisplayName] NVARCHAR(200) NOT NULL,
        [ClientId] NVARCHAR(300) NOT NULL,
        [ClientSecretEncrypted] NVARCHAR(MAX) NULL,
        [AllowedCallbackUrisJson] NVARCHAR(MAX) NOT NULL,
        [UseDiscovery] BIT NOT NULL,
        [DiscoveryUrl] NVARCHAR(500) NULL,
        [Issuer] NVARCHAR(500) NULL,
        [AuthorizationEndpoint] NVARCHAR(1000) NULL,
        [TokenEndpoint] NVARCHAR(1000) NULL,
        [UserInfoEndpoint] NVARCHAR(1000) NULL,
        [JwksUri] NVARCHAR(1000) NULL,
        [MicrosoftTenant] NVARCHAR(200) NULL,
        [ScopesJson] NVARCHAR(MAX) NOT NULL,
        [ClaimMappingJson] NVARCHAR(MAX) NOT NULL,
        [ClientAuthMethod] NVARCHAR(40) NOT NULL,
        [UseUserInfo] BIT NOT NULL,
        [AppleTeamId] NVARCHAR(100) NULL,
        [AppleKeyId] NVARCHAR(100) NULL,
        [ApplePrivateKeyEncrypted] NVARCHAR(MAX) NULL,
        [IsEnabled] BIT NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL
    );
END

GO

IF EXISTS (
    SELECT 1
    FROM sys.tables
    WHERE name = 'SqlOSAuthSocialConnections'
      AND schema_id = SCHEMA_ID('{Schema}')
)
AND NOT EXISTS (
    SELECT 1
    FROM [{Schema}].[SqlOSAuthOidcConnections]
)
BEGIN
    INSERT INTO [{Schema}].[SqlOSAuthOidcConnections] (
        [Id],
        [ProviderType],
        [DisplayName],
        [ClientId],
        [ClientSecretEncrypted],
        [AllowedCallbackUrisJson],
        [UseDiscovery],
        [DiscoveryUrl],
        [Issuer],
        [AuthorizationEndpoint],
        [TokenEndpoint],
        [UserInfoEndpoint],
        [JwksUri],
        [MicrosoftTenant],
        [ScopesJson],
        [ClaimMappingJson],
        [ClientAuthMethod],
        [UseUserInfo],
        [AppleTeamId],
        [AppleKeyId],
        [ApplePrivateKeyEncrypted],
        [IsEnabled],
        [CreatedAt],
        [UpdatedAt]
    )
    SELECT
        [Id],
        [ProviderType],
        [DisplayName],
        [ClientId],
        [ClientSecretEncrypted],
        [AllowedCallbackUrisJson],
        1,
        CASE
            WHEN [ProviderType] = 'Google' THEN 'https://accounts.google.com/.well-known/openid-configuration'
            WHEN [ProviderType] = 'Microsoft' THEN CONCAT('https://login.microsoftonline.com/', COALESCE(NULLIF([MicrosoftTenant], ''), 'common'), '/v2.0/.well-known/openid-configuration')
            ELSE NULL
        END,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        [MicrosoftTenant],
        [ScopesJson],
        '{{"SubjectClaim":"sub","EmailClaim":"email","EmailVerifiedClaim":"email_verified","DisplayNameClaim":"name","FirstNameClaim":"given_name","LastNameClaim":"family_name","PreferredUsernameClaim":"preferred_username"}}',
        'ClientSecretPost',
        1,
        NULL,
        NULL,
        NULL,
        [IsEnabled],
        [CreatedAt],
        [UpdatedAt]
    FROM [{Schema}].[SqlOSAuthSocialConnections];
END

GO

IF COL_LENGTH('{Schema}.SqlOSExternalIdentities', 'OidcConnectionId') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSExternalIdentities]
    ADD [OidcConnectionId] NVARCHAR(64) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSExternalIdentities', 'SocialConnectionId') IS NOT NULL
BEGIN
    UPDATE [{Schema}].[SqlOSExternalIdentities]
    SET [OidcConnectionId] = [SocialConnectionId]
    WHERE [OidcConnectionId] IS NULL
      AND [SocialConnectionId] IS NOT NULL;
END

GO

IF EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE [name] = 'FK_SqlOSExternalIdentities_SocialConnections'
      AND [parent_object_id] = OBJECT_ID('[{Schema}].[SqlOSExternalIdentities]')
)
BEGIN
    ALTER TABLE [{Schema}].[SqlOSExternalIdentities]
    DROP CONSTRAINT [FK_SqlOSExternalIdentities_SocialConnections];
END

GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE [name] = 'FK_SqlOSExternalIdentities_OidcConnections'
      AND [parent_object_id] = OBJECT_ID('[{Schema}].[SqlOSExternalIdentities]')
)
BEGIN
    ALTER TABLE [{Schema}].[SqlOSExternalIdentities]
    ADD CONSTRAINT [FK_SqlOSExternalIdentities_OidcConnections]
        FOREIGN KEY ([OidcConnectionId]) REFERENCES [{Schema}].[SqlOSAuthOidcConnections]([Id]);
END

GO

IF EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE [name] = 'IX_SqlOSExternalIdentities_SocialConnectionId_Subject'
      AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSExternalIdentities]')
)
BEGIN
    DROP INDEX [IX_SqlOSExternalIdentities_SocialConnectionId_Subject]
    ON [{Schema}].[SqlOSExternalIdentities];
END

GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE [name] = 'IX_SqlOSExternalIdentities_OidcConnectionId_Subject'
      AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSExternalIdentities]')
)
BEGIN
    CREATE UNIQUE INDEX [IX_SqlOSExternalIdentities_OidcConnectionId_Subject]
    ON [{Schema}].[SqlOSExternalIdentities]([OidcConnectionId], [Subject])
    WHERE [OidcConnectionId] IS NOT NULL;
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (4);
