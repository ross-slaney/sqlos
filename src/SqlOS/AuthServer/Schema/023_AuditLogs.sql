IF COL_LENGTH('[{Schema}].[SqlOSAuditEvents]', 'ApplicationId') IS NULL
    ALTER TABLE [{Schema}].[SqlOSAuditEvents] ADD [ApplicationId] NVARCHAR(64) NULL;

IF COL_LENGTH('[{Schema}].[SqlOSAuditEvents]', 'ApplicationKey') IS NULL
    ALTER TABLE [{Schema}].[SqlOSAuditEvents] ADD [ApplicationKey] NVARCHAR(200) NULL;

IF COL_LENGTH('[{Schema}].[SqlOSAuditEvents]', 'Source') IS NULL
    ALTER TABLE [{Schema}].[SqlOSAuditEvents] ADD [Source] NVARCHAR(80) NOT NULL CONSTRAINT [DF_SqlOSAuditEvents_Source] DEFAULT ('authserver');

IF COL_LENGTH('[{Schema}].[SqlOSAuditEvents]', 'Action') IS NULL
    ALTER TABLE [{Schema}].[SqlOSAuditEvents] ADD [Action] NVARCHAR(160) NOT NULL CONSTRAINT [DF_SqlOSAuditEvents_Action] DEFAULT ('');

IF COL_LENGTH('[{Schema}].[SqlOSAuditEvents]', 'ActorDisplayName') IS NULL
    ALTER TABLE [{Schema}].[SqlOSAuditEvents] ADD [ActorDisplayName] NVARCHAR(320) NULL;

IF COL_LENGTH('[{Schema}].[SqlOSAuditEvents]', 'TargetsJson') IS NULL
    ALTER TABLE [{Schema}].[SqlOSAuditEvents] ADD [TargetsJson] NVARCHAR(MAX) NOT NULL CONSTRAINT [DF_SqlOSAuditEvents_TargetsJson] DEFAULT ('[]');

IF COL_LENGTH('[{Schema}].[SqlOSAuditEvents]', 'ContextJson') IS NULL
    ALTER TABLE [{Schema}].[SqlOSAuditEvents] ADD [ContextJson] NVARCHAR(MAX) NULL;

IF COL_LENGTH('[{Schema}].[SqlOSAuditEvents]', 'MetadataJson') IS NULL
    ALTER TABLE [{Schema}].[SqlOSAuditEvents] ADD [MetadataJson] NVARCHAR(MAX) NULL;

IF COL_LENGTH('[{Schema}].[SqlOSAuditEvents]', 'IngestedAt') IS NULL
    ALTER TABLE [{Schema}].[SqlOSAuditEvents] ADD [IngestedAt] DATETIME2 NOT NULL CONSTRAINT [DF_SqlOSAuditEvents_IngestedAt] DEFAULT (SYSUTCDATETIME());

IF COL_LENGTH('[{Schema}].[SqlOSAuditEvents]', 'UserAgent') IS NULL
    ALTER TABLE [{Schema}].[SqlOSAuditEvents] ADD [UserAgent] NVARCHAR(512) NULL;

IF COL_LENGTH('[{Schema}].[SqlOSAuditEvents]', 'RequestId') IS NULL
    ALTER TABLE [{Schema}].[SqlOSAuditEvents] ADD [RequestId] NVARCHAR(128) NULL;

IF COL_LENGTH('[{Schema}].[SqlOSAuditEvents]', 'CorrelationId') IS NULL
    ALTER TABLE [{Schema}].[SqlOSAuditEvents] ADD [CorrelationId] NVARCHAR(128) NULL;

IF COL_LENGTH('[{Schema}].[SqlOSAuditEvents]', 'IdempotencyKeyHash') IS NULL
    ALTER TABLE [{Schema}].[SqlOSAuditEvents] ADD [IdempotencyKeyHash] NVARCHAR(128) NULL;

IF COL_LENGTH('[{Schema}].[SqlOSAuditEvents]', 'Action') IS NOT NULL
    UPDATE [{Schema}].[SqlOSAuditEvents]
    SET [Action] = [EventType]
    WHERE NULLIF([Action], '') IS NULL;

IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('[{Schema}].[SqlOSAuditEvents]') AND name = 'EventType' AND max_length < 320)
    ALTER TABLE [{Schema}].[SqlOSAuditEvents] ALTER COLUMN [EventType] NVARCHAR(160) NOT NULL;

IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('[{Schema}].[SqlOSAuditEvents]') AND name = 'ActorId' AND max_length < 256)
    ALTER TABLE [{Schema}].[SqlOSAuditEvents] ALTER COLUMN [ActorId] NVARCHAR(128) NULL;

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SqlOSAuditEvents_OccurredAt' AND object_id = OBJECT_ID('[{Schema}].[SqlOSAuditEvents]'))
    CREATE INDEX [IX_SqlOSAuditEvents_OccurredAt] ON [{Schema}].[SqlOSAuditEvents] ([OccurredAt] DESC);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SqlOSAuditEvents_OrganizationId_OccurredAt' AND object_id = OBJECT_ID('[{Schema}].[SqlOSAuditEvents]'))
    CREATE INDEX [IX_SqlOSAuditEvents_OrganizationId_OccurredAt] ON [{Schema}].[SqlOSAuditEvents] ([OrganizationId], [OccurredAt] DESC);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SqlOSAuditEvents_ApplicationId_OccurredAt' AND object_id = OBJECT_ID('[{Schema}].[SqlOSAuditEvents]'))
    CREATE INDEX [IX_SqlOSAuditEvents_ApplicationId_OccurredAt] ON [{Schema}].[SqlOSAuditEvents] ([ApplicationId], [OccurredAt] DESC);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SqlOSAuditEvents_ApplicationKey_OccurredAt' AND object_id = OBJECT_ID('[{Schema}].[SqlOSAuditEvents]'))
    CREATE INDEX [IX_SqlOSAuditEvents_ApplicationKey_OccurredAt] ON [{Schema}].[SqlOSAuditEvents] ([ApplicationKey], [OccurredAt] DESC);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SqlOSAuditEvents_Source_OccurredAt' AND object_id = OBJECT_ID('[{Schema}].[SqlOSAuditEvents]'))
    CREATE INDEX [IX_SqlOSAuditEvents_Source_OccurredAt] ON [{Schema}].[SqlOSAuditEvents] ([Source], [OccurredAt] DESC);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SqlOSAuditEvents_Action_OccurredAt' AND object_id = OBJECT_ID('[{Schema}].[SqlOSAuditEvents]'))
    CREATE INDEX [IX_SqlOSAuditEvents_Action_OccurredAt] ON [{Schema}].[SqlOSAuditEvents] ([Action], [OccurredAt] DESC);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SqlOSAuditEvents_Actor_OccurredAt' AND object_id = OBJECT_ID('[{Schema}].[SqlOSAuditEvents]'))
    CREATE INDEX [IX_SqlOSAuditEvents_Actor_OccurredAt] ON [{Schema}].[SqlOSAuditEvents] ([ActorType], [ActorId], [OccurredAt] DESC);

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_SqlOSAuditEvents_IdempotencyKeyHash' AND object_id = OBJECT_ID('[{Schema}].[SqlOSAuditEvents]'))
    CREATE UNIQUE INDEX [UX_SqlOSAuditEvents_IdempotencyKeyHash] ON [{Schema}].[SqlOSAuditEvents] ([IdempotencyKeyHash]) WHERE [IdempotencyKeyHash] IS NOT NULL;

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (23);
