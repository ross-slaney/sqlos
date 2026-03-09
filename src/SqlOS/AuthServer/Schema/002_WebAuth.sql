IF COL_LENGTH('{Schema}.SqlOSOrganizations', 'PrimaryDomain') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSOrganizations]
    ADD [PrimaryDomain] NVARCHAR(320) NULL;
END

GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_SqlOSOrganizations_PrimaryDomain'
      AND object_id = OBJECT_ID('[{Schema}].[SqlOSOrganizations]')
)
BEGIN
    CREATE UNIQUE INDEX [IX_SqlOSOrganizations_PrimaryDomain]
    ON [{Schema}].[SqlOSOrganizations]([PrimaryDomain])
    WHERE [PrimaryDomain] IS NOT NULL;
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSSettings' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSSettings] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [RefreshTokenLifetimeMinutes] INT NOT NULL,
        [SessionIdleTimeoutMinutes] INT NOT NULL,
        [SessionAbsoluteLifetimeMinutes] INT NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSAuthorizationRequests' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSAuthorizationRequests] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [ClientApplicationId] NVARCHAR(64) NOT NULL,
        [OrganizationId] NVARCHAR(64) NOT NULL,
        [ConnectionId] NVARCHAR(64) NOT NULL,
        [LoginHintEmail] NVARCHAR(320) NOT NULL,
        [RedirectUri] NVARCHAR(2048) NOT NULL,
        [State] NVARCHAR(256) NOT NULL,
        [CodeChallenge] NVARCHAR(256) NOT NULL,
        [CodeChallengeMethod] NVARCHAR(32) NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [ExpiresAt] DATETIME2 NOT NULL,
        [CompletedAt] DATETIME2 NULL,
        [CancelledAt] DATETIME2 NULL,
        CONSTRAINT [FK_SqlOSAuthorizationRequests_Clients] FOREIGN KEY ([ClientApplicationId]) REFERENCES [{Schema}].[SqlOSClientApplications]([Id]),
        CONSTRAINT [FK_SqlOSAuthorizationRequests_Organizations] FOREIGN KEY ([OrganizationId]) REFERENCES [{Schema}].[SqlOSOrganizations]([Id]),
        CONSTRAINT [FK_SqlOSAuthorizationRequests_SsoConnections] FOREIGN KEY ([ConnectionId]) REFERENCES [{Schema}].[SqlOSSsoConnections]([Id])
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSAuthorizationCodes' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSAuthorizationCodes] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [AuthorizationRequestId] NVARCHAR(64) NOT NULL,
        [UserId] NVARCHAR(64) NOT NULL,
        [ClientApplicationId] NVARCHAR(64) NOT NULL,
        [OrganizationId] NVARCHAR(64) NOT NULL,
        [RedirectUri] NVARCHAR(2048) NOT NULL,
        [State] NVARCHAR(256) NOT NULL,
        [CodeHash] NVARCHAR(128) NOT NULL UNIQUE,
        [CodeChallenge] NVARCHAR(256) NOT NULL,
        [CodeChallengeMethod] NVARCHAR(32) NOT NULL,
        [AuthenticationMethod] NVARCHAR(50) NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [ExpiresAt] DATETIME2 NOT NULL,
        [ConsumedAt] DATETIME2 NULL,
        CONSTRAINT [FK_SqlOSAuthorizationCodes_Requests] FOREIGN KEY ([AuthorizationRequestId]) REFERENCES [{Schema}].[SqlOSAuthorizationRequests]([Id]),
        CONSTRAINT [FK_SqlOSAuthorizationCodes_Users] FOREIGN KEY ([UserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id]),
        CONSTRAINT [FK_SqlOSAuthorizationCodes_Clients] FOREIGN KEY ([ClientApplicationId]) REFERENCES [{Schema}].[SqlOSClientApplications]([Id]),
        CONSTRAINT [FK_SqlOSAuthorizationCodes_Organizations] FOREIGN KEY ([OrganizationId]) REFERENCES [{Schema}].[SqlOSOrganizations]([Id])
    );
END

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (2);
