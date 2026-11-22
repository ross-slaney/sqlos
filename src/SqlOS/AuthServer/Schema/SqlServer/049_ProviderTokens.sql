-- A social login connection can keep the provider's tokens beside each person's
-- external identity (off by default). Tokens are protected with Data Protection.

IF OBJECT_ID(N'[{Schema}].[SqlOSAuthOidcConnections]', N'U') IS NOT NULL
    AND COL_LENGTH('[{Schema}].[SqlOSAuthOidcConnections]', 'StoreProviderTokens') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthOidcConnections]
        ADD [StoreProviderTokens] BIT NOT NULL
            CONSTRAINT [DF_SqlOSAuthOidcConnections_StoreProviderTokens] DEFAULT 0;
END

IF OBJECT_ID(N'[{Schema}].[SqlOSExternalIdentities]', N'U') IS NOT NULL
    AND COL_LENGTH('[{Schema}].[SqlOSExternalIdentities]', 'ProviderAccessTokenEncrypted') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSExternalIdentities] ADD [ProviderAccessTokenEncrypted] NVARCHAR(MAX) NULL;
END

IF OBJECT_ID(N'[{Schema}].[SqlOSExternalIdentities]', N'U') IS NOT NULL
    AND COL_LENGTH('[{Schema}].[SqlOSExternalIdentities]', 'ProviderRefreshTokenEncrypted') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSExternalIdentities] ADD [ProviderRefreshTokenEncrypted] NVARCHAR(MAX) NULL;
END

IF OBJECT_ID(N'[{Schema}].[SqlOSExternalIdentities]', N'U') IS NOT NULL
    AND COL_LENGTH('[{Schema}].[SqlOSExternalIdentities]', 'ProviderAccessTokenExpiresAt') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSExternalIdentities] ADD [ProviderAccessTokenExpiresAt] DATETIME2 NULL;
END

IF OBJECT_ID(N'[{Schema}].[SqlOSExternalIdentities]', N'U') IS NOT NULL
    AND COL_LENGTH('[{Schema}].[SqlOSExternalIdentities]', 'ProviderGrantedScopesJson') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSExternalIdentities] ADD [ProviderGrantedScopesJson] NVARCHAR(MAX) NULL;
END

IF OBJECT_ID(N'[{Schema}].[SqlOSExternalIdentities]', N'U') IS NOT NULL
    AND COL_LENGTH('[{Schema}].[SqlOSExternalIdentities]', 'ProviderTokensUpdatedAt') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSExternalIdentities] ADD [ProviderTokensUpdatedAt] DATETIME2 NULL;
END
