IF COL_LENGTH('{Schema}.SqlOSScimExternalIds', 'UserName') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSScimExternalIds] ADD [UserName] NVARCHAR(450) NULL;
END

IF COL_LENGTH('{Schema}.SqlOSScimExternalIds', 'PrimaryEmail') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSScimExternalIds] ADD [PrimaryEmail] NVARCHAR(320) NULL;
END

IF COL_LENGTH('{Schema}.SqlOSScimExternalIds', 'GivenName') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSScimExternalIds] ADD [GivenName] NVARCHAR(150) NULL;
END

IF COL_LENGTH('{Schema}.SqlOSScimExternalIds', 'FormattedName') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSScimExternalIds] ADD [FormattedName] NVARCHAR(300) NULL;
END

IF COL_LENGTH('{Schema}.SqlOSScimExternalIds', 'FamilyName') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSScimExternalIds] ADD [FamilyName] NVARCHAR(150) NULL;
END

IF COL_LENGTH('{Schema}.SqlOSScimExternalIds', 'DeletedAt') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSScimExternalIds] ADD [DeletedAt] DATETIME2 NULL;
END

IF COL_LENGTH('{Schema}.SqlOSScimExternalIds', 'OwnsUserLifecycle') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSScimExternalIds]
        ADD [OwnsUserLifecycle] BIT NOT NULL
            CONSTRAINT [DF_SqlOSScimExternalIds_OwnsUserLifecycle] DEFAULT 0;
END

IF OBJECT_ID('[{Schema}].[SqlOSScimOperationCommits]', 'U') IS NULL
BEGIN
    CREATE TABLE [{Schema}].[SqlOSScimOperationCommits] (
        [Id] NVARCHAR(64) NOT NULL CONSTRAINT [PK_SqlOSScimOperationCommits] PRIMARY KEY,
        [OccurredAt] DATETIME2 NOT NULL
    );
    CREATE INDEX [IX_SqlOSScimOperationCommits_OccurredAt]
        ON [{Schema}].[SqlOSScimOperationCommits]([OccurredAt]);
END

GO

IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE [object_id] = OBJECT_ID('[{Schema}].[SqlOSScimExternalIds]')
      AND [name] = 'ExternalId'
      AND [is_nullable] = 0)
BEGIN
    IF EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [name] = 'IX_SqlOSScimExternalIds_Connection_Resource_External'
          AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSScimExternalIds]'))
    BEGIN
        DROP INDEX [IX_SqlOSScimExternalIds_Connection_Resource_External]
            ON [{Schema}].[SqlOSScimExternalIds];
    END

    ALTER TABLE [{Schema}].[SqlOSScimExternalIds]
        ALTER COLUMN [ExternalId] NVARCHAR(450) COLLATE Latin1_General_100_BIN2 NULL;
END

GO

UPDATE externalIds
SET [UserName] = COALESCE(externalIds.[UserName],
        NULLIF(LTRIM(RTRIM(users.[DefaultEmail])), ''),
        NULLIF(LTRIM(RTRIM(externalIds.[ExternalId])), '')),
    [PrimaryEmail] = COALESCE(externalIds.[PrimaryEmail], NULLIF(LTRIM(RTRIM(users.[DefaultEmail])), '')),
    [DisplayName] = COALESCE(externalIds.[DisplayName], users.[DisplayName]),
    [FormattedName] = COALESCE(externalIds.[FormattedName], externalIds.[DisplayName], users.[DisplayName])
FROM [{Schema}].[SqlOSScimExternalIds] externalIds
INNER JOIN [{Schema}].[SqlOSUsers] users ON users.[Id] = externalIds.[EntityId]
WHERE externalIds.[ResourceType] = 'User'
  AND (externalIds.[UserName] IS NULL
    OR externalIds.[PrimaryEmail] IS NULL
    OR externalIds.[DisplayName] IS NULL
    OR externalIds.[FormattedName] IS NULL);

;WITH duplicateEntities AS (
    SELECT [Id],
        ROW_NUMBER() OVER (
            PARTITION BY [ConnectionId], [ResourceType], [EntityId]
            ORDER BY [UpdatedAt] DESC, [CreatedAt] DESC, [Id] DESC) AS rowNumber
    FROM [{Schema}].[SqlOSScimExternalIds]
)
DELETE FROM duplicateEntities WHERE rowNumber > 1;

;WITH duplicateExternalIds AS (
    SELECT [Id],
        ROW_NUMBER() OVER (
            PARTITION BY [ConnectionId], [ResourceType], [ExternalId] COLLATE Latin1_General_100_BIN2
            ORDER BY [UpdatedAt] DESC, [CreatedAt] DESC, [Id] DESC) AS rowNumber
    FROM [{Schema}].[SqlOSScimExternalIds]
    WHERE [ExternalId] IS NOT NULL
)
DELETE FROM duplicateExternalIds WHERE rowNumber > 1;

;WITH duplicateUserNames AS (
    SELECT [Id],
        ROW_NUMBER() OVER (
            PARTITION BY [ConnectionId], [ResourceType], [UserName] COLLATE Latin1_General_100_CI_AS
            ORDER BY [UpdatedAt] DESC, [CreatedAt] DESC, [Id] DESC) AS rowNumber
    FROM [{Schema}].[SqlOSScimExternalIds]
    WHERE [UserName] IS NOT NULL
)
UPDATE externalIds
SET [UserName] = CONCAT('sqlos-migrated-', externalIds.[Id])
FROM [{Schema}].[SqlOSScimExternalIds] externalIds
INNER JOIN duplicateUserNames duplicates ON duplicates.[Id] = externalIds.[Id]
WHERE duplicates.rowNumber > 1;

GO

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = 'IX_SqlOSScimExternalIds_Connection_Resource_External'
      AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSScimExternalIds]'))
BEGIN
    DROP INDEX [IX_SqlOSScimExternalIds_Connection_Resource_External]
        ON [{Schema}].[SqlOSScimExternalIds];
END

ALTER TABLE [{Schema}].[SqlOSScimExternalIds]
    ALTER COLUMN [ExternalId] NVARCHAR(450) COLLATE Latin1_General_100_BIN2 NULL;

CREATE UNIQUE INDEX [IX_SqlOSScimExternalIds_Connection_Resource_External]
    ON [{Schema}].[SqlOSScimExternalIds]([ConnectionId], [ResourceType], [ExternalId])
    WHERE [ExternalId] IS NOT NULL;

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = 'IX_SqlOSScimExternalIds_Connection_Resource_Entity'
      AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSScimExternalIds]'))
BEGIN
    DROP INDEX [IX_SqlOSScimExternalIds_Connection_Resource_Entity]
        ON [{Schema}].[SqlOSScimExternalIds];
END

CREATE UNIQUE INDEX [IX_SqlOSScimExternalIds_Connection_Resource_Entity]
    ON [{Schema}].[SqlOSScimExternalIds]([ConnectionId], [ResourceType], [EntityId]);

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = 'IX_SqlOSScimExternalIds_Connection_Resource_UserName'
      AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSScimExternalIds]'))
BEGIN
    DROP INDEX [IX_SqlOSScimExternalIds_Connection_Resource_UserName]
        ON [{Schema}].[SqlOSScimExternalIds];
END

ALTER TABLE [{Schema}].[SqlOSScimExternalIds]
    ALTER COLUMN [UserName] NVARCHAR(450) COLLATE Latin1_General_100_CI_AS NULL;

ALTER TABLE [{Schema}].[SqlOSScimExternalIds]
    ALTER COLUMN [PrimaryEmail] NVARCHAR(320) COLLATE Latin1_General_100_CI_AS NULL;

ALTER TABLE [{Schema}].[SqlOSScimExternalIds]
    ALTER COLUMN [DisplayName] NVARCHAR(300) COLLATE Latin1_General_100_CI_AS NULL;

CREATE UNIQUE INDEX [IX_SqlOSScimExternalIds_Connection_Resource_UserName]
    ON [{Schema}].[SqlOSScimExternalIds]([ConnectionId], [ResourceType], [UserName])
    WHERE [UserName] IS NOT NULL;

GO

SET XACT_ABORT ON;
BEGIN TRY
BEGIN TRANSACTION;

;WITH enabledConnections AS (
    SELECT [Id],
        ROW_NUMBER() OVER (
            PARTITION BY [OrganizationId]
            ORDER BY [UpdatedAt] DESC, [CreatedAt] DESC, [Id] DESC) AS rowNumber
    FROM [{Schema}].[SqlOSScimConnections]
    WHERE [IsEnabled] = 1
)
UPDATE connections
SET [IsEnabled] = 0,
    [UpdatedAt] = SYSUTCDATETIME()
FROM [{Schema}].[SqlOSScimConnections] connections
INNER JOIN enabledConnections ranked ON ranked.[Id] = connections.[Id]
WHERE ranked.rowNumber > 1;

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = 'UX_SqlOSScimConnections_OneEnabledPerOrganization'
      AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSScimConnections]'))
BEGIN
    DROP INDEX [UX_SqlOSScimConnections_OneEnabledPerOrganization]
        ON [{Schema}].[SqlOSScimConnections];
END

CREATE UNIQUE INDEX [UX_SqlOSScimConnections_OneEnabledPerOrganization]
    ON [{Schema}].[SqlOSScimConnections]([OrganizationId])
    WHERE [IsEnabled] = 1;

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH

GO

UPDATE [{Schema}].[SqlOSSchema] SET [Version] = 30;
IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (30);
END
