IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSCalendarConnections' AND schema_id = SCHEMA_ID('{Schema}'))
   AND OBJECT_ID('{Schema}.SqlOSUsers', 'U') IS NOT NULL
   AND OBJECT_ID('{Schema}.SqlOSOrganizations', 'U') IS NOT NULL
   AND OBJECT_ID('{Schema}.SqlOSAuthOidcConnections', 'U') IS NOT NULL
BEGIN
    CREATE TABLE [{Schema}].[SqlOSCalendarConnections] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [ProviderType] NVARCHAR(40) NOT NULL,
        [Mode] NVARCHAR(40) NOT NULL,
        [Status] NVARCHAR(40) NOT NULL,
        [OidcConnectionId] NVARCHAR(64) NOT NULL,
        [UserId] NVARCHAR(64) NULL,
        [OrganizationId] NVARCHAR(64) NULL,
        [DisplayName] NVARCHAR(200) NOT NULL,
        [ProviderAccountEmail] NVARCHAR(320) NULL,
        [ProviderAccountSubject] NVARCHAR(256) NULL,
        [ScopesJson] NVARCHAR(MAX) NOT NULL,
        [AccessTokenEncrypted] NVARCHAR(MAX) NULL,
        [RefreshTokenEncrypted] NVARCHAR(MAX) NULL,
        [AccessTokenExpiresAt] DATETIME2 NULL,
        [LastSyncAt] DATETIME2 NULL,
        [LastError] NVARCHAR(1000) NULL,
        [LastErrorAt] DATETIME2 NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL,
        [RevokedAt] DATETIME2 NULL,
        [RevokedReason] NVARCHAR(160) NULL,
        CONSTRAINT [FK_SqlOSCalendarConnections_Users]
            FOREIGN KEY ([UserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id]),
        CONSTRAINT [FK_SqlOSCalendarConnections_Organizations]
            FOREIGN KEY ([OrganizationId]) REFERENCES [{Schema}].[SqlOSOrganizations]([Id]),
        CONSTRAINT [FK_SqlOSCalendarConnections_OidcConnections]
            FOREIGN KEY ([OidcConnectionId]) REFERENCES [{Schema}].[SqlOSAuthOidcConnections]([Id])
    );

    CREATE INDEX [IX_SqlOSCalendarConnections_UserId_RevokedAt]
        ON [{Schema}].[SqlOSCalendarConnections] ([UserId], [RevokedAt]);
    CREATE INDEX [IX_SqlOSCalendarConnections_OrganizationId_RevokedAt]
        ON [{Schema}].[SqlOSCalendarConnections] ([OrganizationId], [RevokedAt]);
    CREATE INDEX [IX_SqlOSCalendarConnections_Mode_Status]
        ON [{Schema}].[SqlOSCalendarConnections] ([Mode], [Status]);
END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSCalendarSyncStates' AND schema_id = SCHEMA_ID('{Schema}'))
   AND OBJECT_ID('{Schema}.SqlOSCalendarConnections', 'U') IS NOT NULL
BEGIN
    CREATE TABLE [{Schema}].[SqlOSCalendarSyncStates] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [CalendarConnectionId] NVARCHAR(64) NOT NULL,
        [ProviderCalendarId] NVARCHAR(256) NOT NULL,
        [DisplayName] NVARCHAR(200) NULL,
        [IsSyncEnabled] BIT NOT NULL,
        [SyncCursor] NVARCHAR(MAX) NULL,
        [LastSyncStartedAt] DATETIME2 NULL,
        [LastSyncCompletedAt] DATETIME2 NULL,
        [LastSyncStatus] NVARCHAR(40) NULL,
        [LastSyncError] NVARCHAR(1000) NULL,
        [EventCount] INT NOT NULL CONSTRAINT [DF_SqlOSCalendarSyncStates_EventCount] DEFAULT (0),
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL,
        CONSTRAINT [FK_SqlOSCalendarSyncStates_Connections]
            FOREIGN KEY ([CalendarConnectionId]) REFERENCES [{Schema}].[SqlOSCalendarConnections]([Id]) ON DELETE CASCADE
    );

    CREATE UNIQUE INDEX [IX_SqlOSCalendarSyncStates_Connection_Calendar]
        ON [{Schema}].[SqlOSCalendarSyncStates] ([CalendarConnectionId], [ProviderCalendarId]);
END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSCalendarEvents' AND schema_id = SCHEMA_ID('{Schema}'))
   AND OBJECT_ID('{Schema}.SqlOSCalendarConnections', 'U') IS NOT NULL
BEGIN
    CREATE TABLE [{Schema}].[SqlOSCalendarEvents] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [CalendarConnectionId] NVARCHAR(64) NOT NULL,
        [ProviderCalendarId] NVARCHAR(256) NOT NULL,
        [ProviderEventId] NVARCHAR(512) NOT NULL,
        [Subject] NVARCHAR(500) NULL,
        [StartsAtUtc] DATETIME2 NOT NULL,
        [EndsAtUtc] DATETIME2 NOT NULL,
        [IsAllDay] BIT NOT NULL,
        [ShowAs] NVARCHAR(20) NOT NULL,
        [Status] NVARCHAR(20) NOT NULL,
        [Location] NVARCHAR(500) NULL,
        [Origin] NVARCHAR(20) NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL,
        CONSTRAINT [FK_SqlOSCalendarEvents_Connections]
            FOREIGN KEY ([CalendarConnectionId]) REFERENCES [{Schema}].[SqlOSCalendarConnections]([Id]) ON DELETE CASCADE
    );

    CREATE UNIQUE INDEX [IX_SqlOSCalendarEvents_ProviderEvent]
        ON [{Schema}].[SqlOSCalendarEvents] ([CalendarConnectionId], [ProviderCalendarId], [ProviderEventId]);
    CREATE INDEX [IX_SqlOSCalendarEvents_Connection_StartsAtUtc]
        ON [{Schema}].[SqlOSCalendarEvents] ([CalendarConnectionId], [StartsAtUtc]);
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (26);
