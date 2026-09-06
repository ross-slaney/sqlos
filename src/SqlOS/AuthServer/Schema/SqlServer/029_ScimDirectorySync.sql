IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSScimConnections' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSScimConnections] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [OrganizationId] NVARCHAR(64) NOT NULL,
        [SeedKey] NVARCHAR(160) NULL,
        [DisplayName] NVARCHAR(200) NOT NULL,
        [IsEnabled] BIT NOT NULL,
        [TokenHash] NVARCHAR(128) NULL,
        [TokenPrefix] NVARCHAR(24) NULL,
        [TokenRotatedAt] DATETIME2 NULL,
        [TokenLastUsedAt] DATETIME2 NULL,
        [LastSyncAt] DATETIME2 NULL,
        [Source] NVARCHAR(40) NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL
    );

    CREATE UNIQUE INDEX [IX_SqlOSScimConnections_OrganizationId_SeedKey]
        ON [{Schema}].[SqlOSScimConnections]([OrganizationId], [SeedKey])
        WHERE [SeedKey] IS NOT NULL;

    CREATE UNIQUE INDEX [IX_SqlOSScimConnections_TokenHash]
        ON [{Schema}].[SqlOSScimConnections]([TokenHash])
        WHERE [TokenHash] IS NOT NULL;

    CREATE INDEX [IX_SqlOSScimConnections_OrganizationId_IsEnabled]
        ON [{Schema}].[SqlOSScimConnections]([OrganizationId], [IsEnabled]);

    ALTER TABLE [{Schema}].[SqlOSScimConnections]
        ADD CONSTRAINT [FK_SqlOSScimConnections_Organizations_OrganizationId]
            FOREIGN KEY ([OrganizationId]) REFERENCES [{Schema}].[SqlOSOrganizations]([Id]);
END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSScimExternalIds' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSScimExternalIds] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [ConnectionId] NVARCHAR(64) NOT NULL,
        [ResourceType] NVARCHAR(20) NOT NULL,
        [ExternalId] NVARCHAR(450) NOT NULL,
        [EntityId] NVARCHAR(128) NOT NULL,
        [FgaSubjectId] NVARCHAR(128) NULL,
        [DisplayName] NVARCHAR(300) NULL,
        [IsActive] BIT NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL,
        [LastSyncedAt] DATETIME2 NOT NULL
    );

    CREATE UNIQUE INDEX [IX_SqlOSScimExternalIds_Connection_Resource_External]
        ON [{Schema}].[SqlOSScimExternalIds]([ConnectionId], [ResourceType], [ExternalId]);

    CREATE INDEX [IX_SqlOSScimExternalIds_Connection_Resource_Entity]
        ON [{Schema}].[SqlOSScimExternalIds]([ConnectionId], [ResourceType], [EntityId]);

    ALTER TABLE [{Schema}].[SqlOSScimExternalIds]
        ADD CONSTRAINT [FK_SqlOSScimExternalIds_Connections_ConnectionId]
            FOREIGN KEY ([ConnectionId]) REFERENCES [{Schema}].[SqlOSScimConnections]([Id]);
END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSScimGroupMappings' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSScimGroupMappings] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [ConnectionId] NVARCHAR(64) NOT NULL,
        [SourceKey] NVARCHAR(300) NULL,
        [Source] NVARCHAR(40) NOT NULL,
        [MatchType] NVARCHAR(40) NOT NULL,
        [GroupDisplayName] NVARCHAR(300) NULL,
        [GroupExternalId] NVARCHAR(450) NULL,
        [GroupPattern] NVARCHAR(500) NULL,
        [RoleKey] NVARCHAR(120) NOT NULL,
        [ResourceId] NVARCHAR(256) NULL,
        [ResourceIdTemplate] NVARCHAR(500) NULL,
        [Description] NVARCHAR(500) NULL,
        [IsEnabled] BIT NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL
    );

    CREATE UNIQUE INDEX [IX_SqlOSScimGroupMappings_ConnectionId_SourceKey]
        ON [{Schema}].[SqlOSScimGroupMappings]([ConnectionId], [SourceKey])
        WHERE [SourceKey] IS NOT NULL;

    CREATE INDEX [IX_SqlOSScimGroupMappings_ConnectionId_IsEnabled]
        ON [{Schema}].[SqlOSScimGroupMappings]([ConnectionId], [IsEnabled]);

    ALTER TABLE [{Schema}].[SqlOSScimGroupMappings]
        ADD CONSTRAINT [FK_SqlOSScimGroupMappings_Connections_ConnectionId]
            FOREIGN KEY ([ConnectionId]) REFERENCES [{Schema}].[SqlOSScimConnections]([Id]);
END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSScimManagedGrants' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSScimManagedGrants] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [ConnectionId] NVARCHAR(64) NOT NULL,
        [MappingId] NVARCHAR(64) NOT NULL,
        [GroupExternalId] NVARCHAR(450) NOT NULL,
        [FgaGroupId] NVARCHAR(128) NOT NULL,
        [FgaGroupSubjectId] NVARCHAR(128) NOT NULL,
        [GrantId] NVARCHAR(128) NOT NULL,
        [RoleId] NVARCHAR(128) NOT NULL,
        [ResourceId] NVARCHAR(256) NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [RevokedAt] DATETIME2 NULL
    );

    CREATE INDEX [IX_SqlOSScimManagedGrants_Reconcile]
        ON [{Schema}].[SqlOSScimManagedGrants]([ConnectionId], [MappingId], [GroupExternalId], [ResourceId], [RoleId]);

    CREATE INDEX [IX_SqlOSScimManagedGrants_GrantId]
        ON [{Schema}].[SqlOSScimManagedGrants]([GrantId]);

    ALTER TABLE [{Schema}].[SqlOSScimManagedGrants]
        ADD CONSTRAINT [FK_SqlOSScimManagedGrants_Connections_ConnectionId]
            FOREIGN KEY ([ConnectionId]) REFERENCES [{Schema}].[SqlOSScimConnections]([Id]);

    ALTER TABLE [{Schema}].[SqlOSScimManagedGrants]
        ADD CONSTRAINT [FK_SqlOSScimManagedGrants_Mappings_MappingId]
            FOREIGN KEY ([MappingId]) REFERENCES [{Schema}].[SqlOSScimGroupMappings]([Id]);
END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSScimSyncEvents' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSScimSyncEvents] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [ConnectionId] NVARCHAR(64) NOT NULL,
        [OrganizationId] NVARCHAR(64) NOT NULL,
        [ResourceType] NVARCHAR(20) NOT NULL,
        [ResourceId] NVARCHAR(128) NULL,
        [ExternalId] NVARCHAR(450) NULL,
        [Action] NVARCHAR(80) NOT NULL,
        [Result] NVARCHAR(40) NOT NULL,
        [Error] NVARCHAR(1000) NULL,
        [DataJson] NVARCHAR(MAX) NULL,
        [RequestId] NVARCHAR(128) NULL,
        [OccurredAt] DATETIME2 NOT NULL
    );

    CREATE INDEX [IX_SqlOSScimSyncEvents_ConnectionId_OccurredAt]
        ON [{Schema}].[SqlOSScimSyncEvents]([ConnectionId], [OccurredAt] DESC);

    CREATE INDEX [IX_SqlOSScimSyncEvents_OrganizationId_OccurredAt]
        ON [{Schema}].[SqlOSScimSyncEvents]([OrganizationId], [OccurredAt] DESC);

    ALTER TABLE [{Schema}].[SqlOSScimSyncEvents]
        ADD CONSTRAINT [FK_SqlOSScimSyncEvents_Connections_ConnectionId]
            FOREIGN KEY ([ConnectionId]) REFERENCES [{Schema}].[SqlOSScimConnections]([Id]);

    ALTER TABLE [{Schema}].[SqlOSScimSyncEvents]
        ADD CONSTRAINT [FK_SqlOSScimSyncEvents_Organizations_OrganizationId]
            FOREIGN KEY ([OrganizationId]) REFERENCES [{Schema}].[SqlOSOrganizations]([Id]);
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (29);
