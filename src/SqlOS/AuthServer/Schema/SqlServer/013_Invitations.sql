IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSInvitations' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSInvitations] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [OrganizationId] NVARCHAR(64) NOT NULL,
        [InvitedEmail] NVARCHAR(320) NOT NULL,
        [NormalizedEmail] NVARCHAR(320) NOT NULL,
        [Role] NVARCHAR(50) NOT NULL,
        [TokenHash] NVARCHAR(128) NOT NULL,
        [InvitedByUserId] NVARCHAR(64) NULL,
        [ClientApplicationId] NVARCHAR(64) NULL,
        [RedirectUri] NVARCHAR(2048) NULL,
        [Scope] NVARCHAR(1000) NULL,
        [Resource] NVARCHAR(2048) NULL,
        [CustomFieldsJson] NVARCHAR(MAX) NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [ExpiresAt] DATETIME2 NOT NULL,
        [LastSentAt] DATETIME2 NULL,
        [LastSendError] NVARCHAR(500) NULL,
        [AcceptedAt] DATETIME2 NULL,
        [AcceptedByUserId] NVARCHAR(64) NULL,
        [RevokedAt] DATETIME2 NULL,
        [RevokedReason] NVARCHAR(120) NULL,
        [IpAddress] NVARCHAR(128) NULL,
        [UserAgent] NVARCHAR(512) NULL
    );

    CREATE UNIQUE INDEX [IX_SqlOSInvitations_TokenHash]
        ON [{Schema}].[SqlOSInvitations]([TokenHash]);

    CREATE INDEX [IX_SqlOSInvitations_Organization_NormalizedEmail_CreatedAt]
        ON [{Schema}].[SqlOSInvitations]([OrganizationId], [NormalizedEmail], [CreatedAt]);

    CREATE INDEX [IX_SqlOSInvitations_NormalizedEmail_CreatedAt]
        ON [{Schema}].[SqlOSInvitations]([NormalizedEmail], [CreatedAt]);

    CREATE INDEX [IX_SqlOSInvitations_IpAddress_CreatedAt]
        ON [{Schema}].[SqlOSInvitations]([IpAddress], [CreatedAt]);

    CREATE INDEX [IX_SqlOSInvitations_InvitedByUserId_CreatedAt]
        ON [{Schema}].[SqlOSInvitations]([InvitedByUserId], [CreatedAt]);

    CREATE INDEX [IX_SqlOSInvitations_ExpiresAt]
        ON [{Schema}].[SqlOSInvitations]([ExpiresAt]);

    ALTER TABLE [{Schema}].[SqlOSInvitations]
        ADD CONSTRAINT [FK_SqlOSInvitations_Organizations_OrganizationId]
            FOREIGN KEY ([OrganizationId]) REFERENCES [{Schema}].[SqlOSOrganizations]([Id]);

    ALTER TABLE [{Schema}].[SqlOSInvitations]
        ADD CONSTRAINT [FK_SqlOSInvitations_Users_InvitedByUserId]
            FOREIGN KEY ([InvitedByUserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id]);

    ALTER TABLE [{Schema}].[SqlOSInvitations]
        ADD CONSTRAINT [FK_SqlOSInvitations_Users_AcceptedByUserId]
            FOREIGN KEY ([AcceptedByUserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id]);

    ALTER TABLE [{Schema}].[SqlOSInvitations]
        ADD CONSTRAINT [FK_SqlOSInvitations_ClientApplications_ClientApplicationId]
            FOREIGN KEY ([ClientApplicationId]) REFERENCES [{Schema}].[SqlOSClientApplications]([Id]);
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'InvitationId') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
        ADD [InvitationId] NVARCHAR(64) NULL;

    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
        ADD CONSTRAINT [FK_SqlOSAuthorizationRequests_Invitations_InvitationId]
            FOREIGN KEY ([InvitationId]) REFERENCES [{Schema}].[SqlOSInvitations]([Id]);
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (13);
