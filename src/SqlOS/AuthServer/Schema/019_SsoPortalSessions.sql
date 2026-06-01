IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSSsoPortalSessions' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSSsoPortalSessions] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [OrganizationId] NVARCHAR(64) NOT NULL,
        [ConnectionId] NVARCHAR(64) NULL,
        [LinkTokenHash] NVARCHAR(128) NOT NULL,
        [SessionTokenHash] NVARCHAR(128) NULL,
        [Provider] NVARCHAR(40) NULL,
        [ReturnUrl] NVARCHAR(1000) NULL,
        [ActorType] NVARCHAR(80) NOT NULL,
        [CreatedByUserId] NVARCHAR(64) NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [ExpiresAt] DATETIME2 NOT NULL,
        [OpenedAt] DATETIME2 NULL,
        [LastSeenAt] DATETIME2 NULL,
        [RevokedAt] DATETIME2 NULL,
        [RevokedReason] NVARCHAR(160) NULL,
        [IpAddress] NVARCHAR(128) NULL,
        [UserAgent] NVARCHAR(512) NULL,
        [LastTestedAt] DATETIME2 NULL,
        [LastTestStatus] NVARCHAR(40) NULL,
        [LastTestMessage] NVARCHAR(500) NULL
    );

    CREATE UNIQUE INDEX [IX_SqlOSSsoPortalSessions_LinkTokenHash]
        ON [{Schema}].[SqlOSSsoPortalSessions]([LinkTokenHash]);

    CREATE UNIQUE INDEX [IX_SqlOSSsoPortalSessions_SessionTokenHash]
        ON [{Schema}].[SqlOSSsoPortalSessions]([SessionTokenHash])
        WHERE [SessionTokenHash] IS NOT NULL;

    CREATE INDEX [IX_SqlOSSsoPortalSessions_OrganizationId_CreatedAt]
        ON [{Schema}].[SqlOSSsoPortalSessions]([OrganizationId], [CreatedAt]);

    CREATE INDEX [IX_SqlOSSsoPortalSessions_OrganizationId_RevokedAt_ExpiresAt]
        ON [{Schema}].[SqlOSSsoPortalSessions]([OrganizationId], [RevokedAt], [ExpiresAt]);

    ALTER TABLE [{Schema}].[SqlOSSsoPortalSessions]
        ADD CONSTRAINT [FK_SqlOSSsoPortalSessions_Organizations_OrganizationId]
            FOREIGN KEY ([OrganizationId]) REFERENCES [{Schema}].[SqlOSOrganizations]([Id]);

    ALTER TABLE [{Schema}].[SqlOSSsoPortalSessions]
        ADD CONSTRAINT [FK_SqlOSSsoPortalSessions_SsoConnections_ConnectionId]
            FOREIGN KEY ([ConnectionId]) REFERENCES [{Schema}].[SqlOSSsoConnections]([Id]);
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (19);
