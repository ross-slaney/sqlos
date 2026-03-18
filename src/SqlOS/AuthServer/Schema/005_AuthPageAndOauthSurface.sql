IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSAuthPageSettings' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSAuthPageSettings] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [LogoBase64] NVARCHAR(MAX) NULL,
        [PrimaryColor] NVARCHAR(32) NOT NULL,
        [AccentColor] NVARCHAR(32) NOT NULL,
        [BackgroundColor] NVARCHAR(32) NOT NULL,
        [Layout] NVARCHAR(32) NOT NULL,
        [PageTitle] NVARCHAR(200) NOT NULL,
        [PageSubtitle] NVARCHAR(500) NOT NULL,
        [EnablePasswordSignup] BIT NOT NULL,
        [EnabledCredentialTypesJson] NVARCHAR(MAX) NOT NULL,
        [PresentationMode] NVARCHAR(32) NOT NULL DEFAULT 'hosted',
        [UpdatedAt] DATETIME2 NOT NULL
    );
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'Description') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [Description] NVARCHAR(500) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'ClientType') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [ClientType] NVARCHAR(40) NOT NULL CONSTRAINT [DF_SqlOSClientApplications_ClientType] DEFAULT 'public_pkce';
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'RequirePkce') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [RequirePkce] BIT NOT NULL CONSTRAINT [DF_SqlOSClientApplications_RequirePkce] DEFAULT 1;
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'AllowedScopesJson') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [AllowedScopesJson] NVARCHAR(MAX) NOT NULL CONSTRAINT [DF_SqlOSClientApplications_AllowedScopesJson] DEFAULT '[]';
END

GO

IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'IsFirstParty') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [IsFirstParty] BIT NOT NULL CONSTRAINT [DF_SqlOSClientApplications_IsFirstParty] DEFAULT 0;
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'OrganizationId') IS NOT NULL
AND EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID('[{Schema}].[SqlOSAuthorizationRequests]')
      AND [name] = 'OrganizationId'
      AND [is_nullable] = 0
)
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ALTER COLUMN [OrganizationId] NVARCHAR(64) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'ConnectionId') IS NOT NULL
AND EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID('[{Schema}].[SqlOSAuthorizationRequests]')
      AND [name] = 'ConnectionId'
      AND [is_nullable] = 0
)
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ALTER COLUMN [ConnectionId] NVARCHAR(64) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'LoginHintEmail') IS NOT NULL
AND EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID('[{Schema}].[SqlOSAuthorizationRequests]')
      AND [name] = 'LoginHintEmail'
      AND [is_nullable] = 0
)
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ALTER COLUMN [LoginHintEmail] NVARCHAR(320) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'Scope') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ADD [Scope] NVARCHAR(1000) NOT NULL CONSTRAINT [DF_SqlOSAuthorizationRequests_Scope] DEFAULT '';
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'Resource') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ADD [Resource] NVARCHAR(2048) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'Nonce') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ADD [Nonce] NVARCHAR(256) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'Prompt') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ADD [Prompt] NVARCHAR(256) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'ResolvedAuthMethod') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ADD [ResolvedAuthMethod] NVARCHAR(50) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'ResolvedOrganizationId') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ADD [ResolvedOrganizationId] NVARCHAR(64) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'ResolvedConnectionId') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ADD [ResolvedConnectionId] NVARCHAR(64) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthorizationCodes', 'OrganizationId') IS NOT NULL
AND EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID('[{Schema}].[SqlOSAuthorizationCodes]')
      AND [name] = 'OrganizationId'
      AND [is_nullable] = 0
)
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationCodes]
    ALTER COLUMN [OrganizationId] NVARCHAR(64) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthorizationCodes', 'Scope') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationCodes]
    ADD [Scope] NVARCHAR(1000) NOT NULL CONSTRAINT [DF_SqlOSAuthorizationCodes_Scope] DEFAULT '';
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthorizationCodes', 'Resource') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationCodes]
    ADD [Resource] NVARCHAR(2048) NULL;
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (5);
