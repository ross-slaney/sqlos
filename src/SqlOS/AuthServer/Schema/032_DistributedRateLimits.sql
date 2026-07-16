IF OBJECT_ID('[{Schema}].[SqlOSRateLimitBuckets]', 'U') IS NULL
BEGIN
    CREATE TABLE [{Schema}].[SqlOSRateLimitBuckets] (
        [Scope] NVARCHAR(64) NOT NULL,
        [BucketKey] NVARCHAR(384) NOT NULL,
        [WindowStartedAt] DATETIME2 NOT NULL,
        [Count] INT NOT NULL,
        [LockedUntil] DATETIME2 NULL,
        [UpdatedAt] DATETIME2 NOT NULL,
        CONSTRAINT [PK_SqlOSRateLimitBuckets] PRIMARY KEY ([Scope], [BucketKey]),
        CONSTRAINT [CK_SqlOSRateLimitBuckets_Count] CHECK ([Count] >= 0)
    );

    CREATE INDEX [IX_SqlOSRateLimitBuckets_UpdatedAt]
        ON [{Schema}].[SqlOSRateLimitBuckets] ([UpdatedAt]);
END
