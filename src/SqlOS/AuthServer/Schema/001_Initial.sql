IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{Schema}')
BEGIN
    EXEC('CREATE SCHEMA [{Schema}]');
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSOrganizations' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSOrganizations] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [Slug] NVARCHAR(120) NOT NULL UNIQUE,
        [Name] NVARCHAR(200) NOT NULL,
        [IsActive] BIT NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSUsers' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSUsers] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [DisplayName] NVARCHAR(200) NOT NULL,
        [DefaultEmail] NVARCHAR(320) NULL,
        [IsActive] BIT NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSUserEmails' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSUserEmails] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [UserId] NVARCHAR(64) NOT NULL,
        [Email] NVARCHAR(320) NOT NULL,
        [NormalizedEmail] NVARCHAR(320) NOT NULL UNIQUE,
        [IsPrimary] BIT NOT NULL,
        [IsVerified] BIT NOT NULL,
        [VerifiedAt] DATETIME2 NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        CONSTRAINT [FK_SqlOSUserEmails_Users] FOREIGN KEY ([UserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id])
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSCredentials' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSCredentials] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [UserId] NVARCHAR(64) NOT NULL,
        [Type] NVARCHAR(50) NOT NULL,
        [SecretHash] NVARCHAR(MAX) NOT NULL,
        [SecretVersion] INT NOT NULL,
        [LastUsedAt] DATETIME2 NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [RevokedAt] DATETIME2 NULL,
        CONSTRAINT [FK_SqlOSCredentials_Users] FOREIGN KEY ([UserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id])
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSMemberships' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSMemberships] (
        [OrganizationId] NVARCHAR(64) NOT NULL,
        [UserId] NVARCHAR(64) NOT NULL,
        [Role] NVARCHAR(50) NOT NULL,
        [IsActive] BIT NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        CONSTRAINT [PK_SqlOSMemberships] PRIMARY KEY ([OrganizationId], [UserId]),
        CONSTRAINT [FK_SqlOSMemberships_Organizations] FOREIGN KEY ([OrganizationId]) REFERENCES [{Schema}].[SqlOSOrganizations]([Id]),
        CONSTRAINT [FK_SqlOSMemberships_Users] FOREIGN KEY ([UserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id])
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSSsoConnections' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSSsoConnections] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [OrganizationId] NVARCHAR(64) NOT NULL,
        [DisplayName] NVARCHAR(200) NOT NULL,
        [IsEnabled] BIT NOT NULL,
        [IdentityProviderEntityId] NVARCHAR(400) NOT NULL,
        [SingleSignOnUrl] NVARCHAR(1024) NOT NULL,
        [X509CertificatePem] NVARCHAR(MAX) NOT NULL,
        [NameIdFormat] NVARCHAR(256) NULL,
        [EmailAttributeName] NVARCHAR(128) NOT NULL,
        [FirstNameAttributeName] NVARCHAR(128) NOT NULL,
        [LastNameAttributeName] NVARCHAR(128) NOT NULL,
        [AutoProvisionUsers] BIT NOT NULL,
        [AutoLinkByEmail] BIT NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL,
        CONSTRAINT [FK_SqlOSSsoConnections_Organizations] FOREIGN KEY ([OrganizationId]) REFERENCES [{Schema}].[SqlOSOrganizations]([Id])
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSExternalIdentities' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSExternalIdentities] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [UserId] NVARCHAR(64) NOT NULL,
        [ConnectionId] NVARCHAR(64) NOT NULL,
        [Issuer] NVARCHAR(400) NOT NULL,
        [Subject] NVARCHAR(400) NOT NULL,
        [Email] NVARCHAR(320) NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        CONSTRAINT [UQ_SqlOSExternalIdentities_Connection_Subject] UNIQUE ([ConnectionId], [Subject]),
        CONSTRAINT [FK_SqlOSExternalIdentities_Users] FOREIGN KEY ([UserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id]),
        CONSTRAINT [FK_SqlOSExternalIdentities_SsoConnections] FOREIGN KEY ([ConnectionId]) REFERENCES [{Schema}].[SqlOSSsoConnections]([Id])
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSClientApplications' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSClientApplications] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [ClientId] NVARCHAR(120) NOT NULL UNIQUE,
        [Name] NVARCHAR(200) NOT NULL,
        [Audience] NVARCHAR(200) NOT NULL,
        [RedirectUrisJson] NVARCHAR(MAX) NOT NULL,
        [IsActive] BIT NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSSessions' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSSessions] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [UserId] NVARCHAR(64) NOT NULL,
        [AuthenticationMethod] NVARCHAR(50) NULL,
        [ClientApplicationId] NVARCHAR(64) NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [LastSeenAt] DATETIME2 NOT NULL,
        [IdleExpiresAt] DATETIME2 NOT NULL,
        [AbsoluteExpiresAt] DATETIME2 NOT NULL,
        [RevokedAt] DATETIME2 NULL,
        [RevocationReason] NVARCHAR(200) NULL,
        [UserAgent] NVARCHAR(1024) NULL,
        [IpAddress] NVARCHAR(128) NULL,
        CONSTRAINT [FK_SqlOSSessions_Users] FOREIGN KEY ([UserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id]),
        CONSTRAINT [FK_SqlOSSessions_Clients] FOREIGN KEY ([ClientApplicationId]) REFERENCES [{Schema}].[SqlOSClientApplications]([Id])
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSRefreshTokens' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSRefreshTokens] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [SessionId] NVARCHAR(64) NOT NULL,
        [TokenHash] NVARCHAR(128) NOT NULL UNIQUE,
        [FamilyId] NVARCHAR(64) NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [ExpiresAt] DATETIME2 NOT NULL,
        [ConsumedAt] DATETIME2 NULL,
        [RevokedAt] DATETIME2 NULL,
        [ReplacedByTokenId] NVARCHAR(64) NULL,
        CONSTRAINT [FK_SqlOSRefreshTokens_Sessions] FOREIGN KEY ([SessionId]) REFERENCES [{Schema}].[SqlOSSessions]([Id])
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSSigningKeys' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSSigningKeys] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [Kid] NVARCHAR(120) NOT NULL UNIQUE,
        [Algorithm] NVARCHAR(20) NOT NULL,
        [PublicKeyPem] NVARCHAR(MAX) NOT NULL,
        [PrivateKeyPem] NVARCHAR(MAX) NOT NULL,
        [IsActive] BIT NOT NULL,
        [ActivatedAt] DATETIME2 NOT NULL,
        [RetiredAt] DATETIME2 NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSTemporaryTokens' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSTemporaryTokens] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [Purpose] NVARCHAR(80) NOT NULL,
        [TokenHash] NVARCHAR(128) NOT NULL UNIQUE,
        [UserId] NVARCHAR(64) NULL,
        [ClientApplicationId] NVARCHAR(64) NULL,
        [OrganizationId] NVARCHAR(64) NULL,
        [PayloadJson] NVARCHAR(MAX) NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [ExpiresAt] DATETIME2 NOT NULL,
        [ConsumedAt] DATETIME2 NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSAuditEvents' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSAuditEvents] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [OrganizationId] NVARCHAR(64) NULL,
        [UserId] NVARCHAR(64) NULL,
        [SessionId] NVARCHAR(64) NULL,
        [EventType] NVARCHAR(120) NOT NULL,
        [ActorType] NVARCHAR(80) NOT NULL,
        [ActorId] NVARCHAR(64) NULL,
        [OccurredAt] DATETIME2 NOT NULL,
        [IpAddress] NVARCHAR(128) NULL,
        [DataJson] NVARCHAR(MAX) NULL
    );
END

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (1);
