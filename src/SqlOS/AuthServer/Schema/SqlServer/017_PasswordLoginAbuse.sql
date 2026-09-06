IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSPasswordLoginBuckets' AND schema_id = SCHEMA_ID('{Schema}'))
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

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (17);
