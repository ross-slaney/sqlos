IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'AccessMode') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [AccessMode] NVARCHAR(40) NOT NULL
        CONSTRAINT [DF_SqlOSClientApplications_AccessMode] DEFAULT 'all_organizations';
END

GO

IF COL_LENGTH('{Schema}.SqlOSSessions', 'OrganizationId') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSSessions]
    ADD [OrganizationId] NVARCHAR(64) NULL;
END

GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = 'FK_SqlOSSessions_Organizations'
      AND parent_object_id = OBJECT_ID('{Schema}.SqlOSSessions')
)
BEGIN
    ALTER TABLE [{Schema}].[SqlOSSessions]
    ADD CONSTRAINT [FK_SqlOSSessions_Organizations]
    FOREIGN KEY ([OrganizationId]) REFERENCES [{Schema}].[SqlOSOrganizations]([Id]);
END

GO

IF OBJECT_ID('{Schema}.SqlOSApplicationAssignments', 'U') IS NULL
BEGIN
    CREATE TABLE [{Schema}].[SqlOSApplicationAssignments]
    (
        [Id] NVARCHAR(64) NOT NULL,
        [ClientApplicationId] NVARCHAR(64) NOT NULL,
        [OrganizationId] NVARCHAR(64) NULL,
        [PrincipalType] NVARCHAR(40) NOT NULL,
        [PrincipalId] NVARCHAR(128) NULL,
        [RoleKey] NVARCHAR(80) NULL,
        [Access] NVARCHAR(20) NOT NULL,
        [Reason] NVARCHAR(500) NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [CreatedByActorType] NVARCHAR(80) NULL,
        [CreatedByActorId] NVARCHAR(128) NULL,
        [RevokedAt] DATETIME2 NULL,
        [RevokedByActorType] NVARCHAR(80) NULL,
        [RevokedByActorId] NVARCHAR(128) NULL,
        CONSTRAINT [PK_SqlOSApplicationAssignments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SqlOSApplicationAssignments_ClientApplication] FOREIGN KEY ([ClientApplicationId]) REFERENCES [{Schema}].[SqlOSClientApplications]([Id]),
        CONSTRAINT [FK_SqlOSApplicationAssignments_Organization] FOREIGN KEY ([OrganizationId]) REFERENCES [{Schema}].[SqlOSOrganizations]([Id])
    );
END

GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_SqlOSClientApplications_AccessMode'
      AND object_id = OBJECT_ID('{Schema}.SqlOSClientApplications')
)
BEGIN
    CREATE INDEX [IX_SqlOSClientApplications_AccessMode]
    ON [{Schema}].[SqlOSClientApplications]([AccessMode]);
END

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_SqlOSApplicationAssignments_Target'
      AND object_id = OBJECT_ID('{Schema}.SqlOSApplicationAssignments')
)
BEGIN
    CREATE INDEX [IX_SqlOSApplicationAssignments_Target]
    ON [{Schema}].[SqlOSApplicationAssignments]([ClientApplicationId], [PrincipalType], [PrincipalId], [OrganizationId], [RoleKey], [RevokedAt]);
END

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_SqlOSApplicationAssignments_ClientApplicationId_RevokedAt'
      AND object_id = OBJECT_ID('{Schema}.SqlOSApplicationAssignments')
)
BEGIN
    CREATE INDEX [IX_SqlOSApplicationAssignments_ClientApplicationId_RevokedAt]
    ON [{Schema}].[SqlOSApplicationAssignments]([ClientApplicationId], [RevokedAt]);
END

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_SqlOSApplicationAssignments_OrganizationId_RevokedAt'
      AND object_id = OBJECT_ID('{Schema}.SqlOSApplicationAssignments')
)
BEGIN
    CREATE INDEX [IX_SqlOSApplicationAssignments_OrganizationId_RevokedAt]
    ON [{Schema}].[SqlOSApplicationAssignments]([OrganizationId], [RevokedAt]);
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (17);
