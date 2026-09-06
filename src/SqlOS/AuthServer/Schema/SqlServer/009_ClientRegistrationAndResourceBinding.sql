IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID('[{Schema}].[SqlOSClientApplications]')
      AND [name] = 'ClientId'
      AND [max_length] < 1700
)
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ALTER COLUMN [ClientId] NVARCHAR(850) NOT NULL;
END

GO

IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID('[{Schema}].[SqlOSClientApplications]')
      AND [name] = 'Audience'
      AND [max_length] < 1700
)
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ALTER COLUMN [Audience] NVARCHAR(850) NOT NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'RegistrationSource') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [RegistrationSource] NVARCHAR(20) NOT NULL
        CONSTRAINT [DF_SqlOSClientApplications_RegistrationSource] DEFAULT 'manual';
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'TokenEndpointAuthMethod') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [TokenEndpointAuthMethod] NVARCHAR(60) NOT NULL
        CONSTRAINT [DF_SqlOSClientApplications_TokenEndpointAuthMethod] DEFAULT 'none';
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'GrantTypesJson') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [GrantTypesJson] NVARCHAR(MAX) NOT NULL
        CONSTRAINT [DF_SqlOSClientApplications_GrantTypesJson] DEFAULT N'["authorization_code","refresh_token"]';
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'ResponseTypesJson') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [ResponseTypesJson] NVARCHAR(MAX) NOT NULL
        CONSTRAINT [DF_SqlOSClientApplications_ResponseTypesJson] DEFAULT N'["code"]';
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'MetadataDocumentUrl') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [MetadataDocumentUrl] NVARCHAR(850) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'ClientUri') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [ClientUri] NVARCHAR(850) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'LogoUri') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [LogoUri] NVARCHAR(850) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'SoftwareId') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [SoftwareId] NVARCHAR(200) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'SoftwareVersion') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [SoftwareVersion] NVARCHAR(120) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'MetadataJson') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [MetadataJson] NVARCHAR(MAX) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'MetadataFetchedAt') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [MetadataFetchedAt] DATETIME2 NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'MetadataExpiresAt') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [MetadataExpiresAt] DATETIME2 NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'MetadataEtag') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [MetadataEtag] NVARCHAR(256) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'MetadataLastModifiedAt') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [MetadataLastModifiedAt] DATETIME2 NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'LastSeenAt') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [LastSeenAt] DATETIME2 NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'DisabledAt') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [DisabledAt] DATETIME2 NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'DisabledReason') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [DisabledReason] NVARCHAR(500) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSSessions', 'Resource') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSSessions]
    ADD [Resource] NVARCHAR(2048) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSSessions', 'EffectiveAudience') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSSessions]
    ADD [EffectiveAudience] NVARCHAR(2048) NULL;
END

GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE [name] = 'IX_SqlOSClientApplications_RegistrationSource'
      AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSClientApplications]')
)
BEGIN
    CREATE INDEX [IX_SqlOSClientApplications_RegistrationSource]
        ON [{Schema}].[SqlOSClientApplications] ([RegistrationSource]);
END

GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE [name] = 'IX_SqlOSClientApplications_IsActive_RegistrationSource'
      AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSClientApplications]')
)
BEGIN
    CREATE INDEX [IX_SqlOSClientApplications_IsActive_RegistrationSource]
        ON [{Schema}].[SqlOSClientApplications] ([IsActive], [RegistrationSource]);
END

GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE [name] = 'IX_SqlOSClientApplications_MetadataDocumentUrl'
      AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSClientApplications]')
)
BEGIN
    CREATE INDEX [IX_SqlOSClientApplications_MetadataDocumentUrl]
        ON [{Schema}].[SqlOSClientApplications] ([MetadataDocumentUrl]);
END

GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE [name] = 'IX_SqlOSClientApplications_LastSeenAt'
      AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSClientApplications]')
)
BEGIN
    CREATE INDEX [IX_SqlOSClientApplications_LastSeenAt]
        ON [{Schema}].[SqlOSClientApplications] ([LastSeenAt]);
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (9);
