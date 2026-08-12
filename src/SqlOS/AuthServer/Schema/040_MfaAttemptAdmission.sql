IF OBJECT_ID('[{Schema}].[SqlOSUsers]', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSMfaAttemptBuckets' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSMfaAttemptBuckets] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [Scope] NVARCHAR(40) NOT NULL,
        [BucketKey] NVARCHAR(512) NOT NULL,
        [AttemptCount] INT NOT NULL CONSTRAINT [DF_SqlOSMfaAttemptBuckets_AttemptCount] DEFAULT 0,
        [WindowStartedAt] DATETIME2 NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL
    );

    CREATE UNIQUE INDEX [IX_SqlOSMfaAttemptBuckets_Scope_BucketKey]
        ON [{Schema}].[SqlOSMfaAttemptBuckets]([Scope], [BucketKey]);
END

IF OBJECT_ID('[{Schema}].[SqlOSMfaAttemptBuckets]', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSMfaAttemptReservations' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSMfaAttemptReservations] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [CreatedAt] DATETIME2 NOT NULL,
        [ExpiresAt] DATETIME2 NOT NULL
    );

    CREATE INDEX [IX_SqlOSMfaAttemptReservations_ExpiresAt]
        ON [{Schema}].[SqlOSMfaAttemptReservations]([ExpiresAt]);
END

IF OBJECT_ID('[{Schema}].[SqlOSMfaAttemptBuckets]', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSMfaAttemptReservationBuckets' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSMfaAttemptReservationBuckets] (
        [ReservationId] NVARCHAR(64) NOT NULL,
        [BucketId] NVARCHAR(64) NOT NULL,
        CONSTRAINT [PK_SqlOSMfaAttemptReservationBuckets] PRIMARY KEY ([ReservationId], [BucketId]),
        CONSTRAINT [FK_SqlOSMfaAttemptReservationBuckets_Reservations_ReservationId]
            FOREIGN KEY ([ReservationId]) REFERENCES [{Schema}].[SqlOSMfaAttemptReservations]([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_SqlOSMfaAttemptReservationBuckets_Buckets_BucketId]
            FOREIGN KEY ([BucketId]) REFERENCES [{Schema}].[SqlOSMfaAttemptBuckets]([Id]) ON DELETE CASCADE
    );

    CREATE INDEX [IX_SqlOSMfaAttemptReservationBuckets_BucketId]
        ON [{Schema}].[SqlOSMfaAttemptReservationBuckets]([BucketId]);
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (40);
