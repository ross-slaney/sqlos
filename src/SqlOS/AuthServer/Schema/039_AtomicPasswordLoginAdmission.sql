-- Older test/repair installations can carry a schema version without the historical
-- password bucket table. Repair it when the user prerequisite exists; otherwise keep
-- this migration compatible with intentionally partial schemas used for upgrade checks.
IF OBJECT_ID('[{Schema}].[SqlOSPasswordLoginBuckets]', 'U') IS NULL
   AND OBJECT_ID('[{Schema}].[SqlOSUsers]', 'U') IS NOT NULL
BEGIN
    CREATE TABLE [{Schema}].[SqlOSPasswordLoginBuckets] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [Scope] NVARCHAR(40) NOT NULL,
        [BucketKey] NVARCHAR(512) NOT NULL,
        [NormalizedEmail] NVARCHAR(320) NULL,
        [UserId] NVARCHAR(64) NULL,
        [ClientKey] NVARCHAR(850) NULL,
        [IpAddress] NVARCHAR(128) NULL,
        [UserAgentHash] NVARCHAR(128) NULL,
        [FailureCount] INT NOT NULL CONSTRAINT [DF_SqlOSPasswordLoginBuckets_FailureCount] DEFAULT 0,
        [WindowStartedAt] DATETIME2 NULL,
        [LastFailureAt] DATETIME2 NULL,
        [LastSuccessAt] DATETIME2 NULL,
        [LockedUntil] DATETIME2 NULL,
        [LockoutReason] NVARCHAR(120) NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL
    );

    CREATE UNIQUE INDEX [IX_SqlOSPasswordLoginBuckets_Scope_BucketKey]
        ON [{Schema}].[SqlOSPasswordLoginBuckets]([Scope], [BucketKey]);
    CREATE INDEX [IX_SqlOSPasswordLoginBuckets_NormalizedEmail_UpdatedAt]
        ON [{Schema}].[SqlOSPasswordLoginBuckets]([NormalizedEmail], [UpdatedAt]);
    CREATE INDEX [IX_SqlOSPasswordLoginBuckets_UserId_UpdatedAt]
        ON [{Schema}].[SqlOSPasswordLoginBuckets]([UserId], [UpdatedAt]);
    CREATE INDEX [IX_SqlOSPasswordLoginBuckets_IpAddress_UpdatedAt]
        ON [{Schema}].[SqlOSPasswordLoginBuckets]([IpAddress], [UpdatedAt]);
    CREATE INDEX [IX_SqlOSPasswordLoginBuckets_ClientKey_UpdatedAt]
        ON [{Schema}].[SqlOSPasswordLoginBuckets]([ClientKey], [UpdatedAt]);
    CREATE INDEX [IX_SqlOSPasswordLoginBuckets_LockedUntil]
        ON [{Schema}].[SqlOSPasswordLoginBuckets]([LockedUntil]);

    ALTER TABLE [{Schema}].[SqlOSPasswordLoginBuckets]
        ADD CONSTRAINT [FK_SqlOSPasswordLoginBuckets_Users_UserId]
            FOREIGN KEY ([UserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id]);
END

IF OBJECT_ID('[{Schema}].[SqlOSPasswordLoginBuckets]', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSPasswordLoginReservations' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSPasswordLoginReservations] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [CreatedAt] DATETIME2 NOT NULL,
        [ExpiresAt] DATETIME2 NOT NULL
    );

    CREATE INDEX [IX_SqlOSPasswordLoginReservations_ExpiresAt]
        ON [{Schema}].[SqlOSPasswordLoginReservations]([ExpiresAt]);
END

IF OBJECT_ID('[{Schema}].[SqlOSPasswordLoginBuckets]', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSPasswordLoginReservationBuckets' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSPasswordLoginReservationBuckets] (
        [ReservationId] NVARCHAR(64) NOT NULL,
        [BucketId] NVARCHAR(64) NOT NULL,
        CONSTRAINT [PK_SqlOSPasswordLoginReservationBuckets] PRIMARY KEY ([ReservationId], [BucketId]),
        CONSTRAINT [FK_SqlOSPasswordLoginReservationBuckets_Reservations_ReservationId]
            FOREIGN KEY ([ReservationId]) REFERENCES [{Schema}].[SqlOSPasswordLoginReservations]([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_SqlOSPasswordLoginReservationBuckets_Buckets_BucketId]
            FOREIGN KEY ([BucketId]) REFERENCES [{Schema}].[SqlOSPasswordLoginBuckets]([Id]) ON DELETE CASCADE
    );

    CREATE INDEX [IX_SqlOSPasswordLoginReservationBuckets_BucketId]
        ON [{Schema}].[SqlOSPasswordLoginReservationBuckets]([BucketId]);
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (39);
