IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSMfaAttemptBuckets' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSMfaAttemptBuckets] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [Scope] NVARCHAR(40) NOT NULL,
        [BucketKey] NVARCHAR(128) NOT NULL,
        [AttemptCount] INT NOT NULL,
        [WindowStartedAt] DATETIME2 NOT NULL,
        [LastAttemptAt] DATETIME2 NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL
    );

    CREATE UNIQUE INDEX [IX_SqlOSMfaAttemptBuckets_Scope_BucketKey]
        ON [{Schema}].[SqlOSMfaAttemptBuckets]([Scope], [BucketKey]);

    CREATE INDEX [IX_SqlOSMfaAttemptBuckets_LastAttemptAt]
        ON [{Schema}].[SqlOSMfaAttemptBuckets]([LastAttemptAt]);

END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSMfaAttemptReservations' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSMfaAttemptReservations] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [OperationId] NVARCHAR(64) NOT NULL,
        [Scope] NVARCHAR(40) NOT NULL,
        [BucketKey] NVARCHAR(128) NOT NULL,
        [WindowStartedAt] DATETIME2 NOT NULL,
        [ReleasedAt] DATETIME2 NULL,
        [CreatedAt] DATETIME2 NOT NULL
    );

    CREATE UNIQUE INDEX [IX_SqlOSMfaAttemptReservations_OperationId_Scope_BucketKey]
        ON [{Schema}].[SqlOSMfaAttemptReservations]([OperationId], [Scope], [BucketKey]);

    CREATE INDEX [IX_SqlOSMfaAttemptReservations_CreatedAt]
        ON [{Schema}].[SqlOSMfaAttemptReservations]([CreatedAt]);
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (39);
