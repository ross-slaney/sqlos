IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSOrganizationDomains' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSOrganizationDomains] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [OrganizationId] NVARCHAR(64) NOT NULL,
        [Domain] NVARCHAR(320) NOT NULL,
        [Status] NVARCHAR(50) NOT NULL,
        [VerificationToken] NVARCHAR(160) NULL,
        [CreatedByUserId] NVARCHAR(64) NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL,
        [VerifiedAt] DATETIME2 NULL,
        [LastCheckedAt] DATETIME2 NULL,
        [RevokedAt] DATETIME2 NULL,
        [LastError] NVARCHAR(1000) NULL
    );

    CREATE UNIQUE INDEX [IX_SqlOSOrganizationDomains_OrganizationId_Domain]
        ON [{Schema}].[SqlOSOrganizationDomains]([OrganizationId], [Domain])
        WHERE [RevokedAt] IS NULL;

    CREATE INDEX [IX_SqlOSOrganizationDomains_Domain_Status]
        ON [{Schema}].[SqlOSOrganizationDomains]([Domain], [Status]);

    CREATE INDEX [IX_SqlOSOrganizationDomains_OrganizationId_Status]
        ON [{Schema}].[SqlOSOrganizationDomains]([OrganizationId], [Status]);

    ALTER TABLE [{Schema}].[SqlOSOrganizationDomains]
        ADD CONSTRAINT [FK_SqlOSOrganizationDomains_Organizations_OrganizationId]
            FOREIGN KEY ([OrganizationId]) REFERENCES [{Schema}].[SqlOSOrganizations]([Id]);
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (20);
