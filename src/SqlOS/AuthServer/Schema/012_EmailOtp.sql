IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSEmailOtpChallenges' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSEmailOtpChallenges] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [ChallengeTokenHash] NVARCHAR(128) NOT NULL,
        [CodeHash] NVARCHAR(128) NOT NULL,
        [Email] NVARCHAR(320) NOT NULL,
        [NormalizedEmail] NVARCHAR(320) NOT NULL,
        [UserId] NVARCHAR(64) NULL,
        [UserEmailId] NVARCHAR(64) NULL,
        [AuthorizationRequestId] NVARCHAR(64) NULL,
        [ClientApplicationId] NVARCHAR(64) NULL,
        [RequestedOrganizationId] NVARCHAR(64) NULL,
        [AttemptCount] INT NOT NULL CONSTRAINT [DF_SqlOSEmailOtpChallenges_AttemptCount] DEFAULT 0,
        [MaxAttempts] INT NOT NULL CONSTRAINT [DF_SqlOSEmailOtpChallenges_MaxAttempts] DEFAULT 5,
        [CreatedAt] DATETIME2 NOT NULL,
        [ExpiresAt] DATETIME2 NOT NULL,
        [LastSentAt] DATETIME2 NOT NULL,
        [ConsumedAt] DATETIME2 NULL,
        [InvalidatedAt] DATETIME2 NULL,
        [InvalidatedReason] NVARCHAR(120) NULL,
        [IpAddress] NVARCHAR(128) NULL,
        [UserAgent] NVARCHAR(512) NULL
    );

    CREATE UNIQUE INDEX [IX_SqlOSEmailOtpChallenges_ChallengeTokenHash]
        ON [{Schema}].[SqlOSEmailOtpChallenges]([ChallengeTokenHash]);

    CREATE INDEX [IX_SqlOSEmailOtpChallenges_NormalizedEmail_CreatedAt]
        ON [{Schema}].[SqlOSEmailOtpChallenges]([NormalizedEmail], [CreatedAt]);

    ALTER TABLE [{Schema}].[SqlOSEmailOtpChallenges]
        ADD CONSTRAINT [FK_SqlOSEmailOtpChallenges_Users_UserId]
            FOREIGN KEY ([UserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id]);

    ALTER TABLE [{Schema}].[SqlOSEmailOtpChallenges]
        ADD CONSTRAINT [FK_SqlOSEmailOtpChallenges_UserEmails_UserEmailId]
            FOREIGN KEY ([UserEmailId]) REFERENCES [{Schema}].[SqlOSUserEmails]([Id]);

    ALTER TABLE [{Schema}].[SqlOSEmailOtpChallenges]
        ADD CONSTRAINT [FK_SqlOSEmailOtpChallenges_AuthorizationRequests_AuthorizationRequestId]
            FOREIGN KEY ([AuthorizationRequestId]) REFERENCES [{Schema}].[SqlOSAuthorizationRequests]([Id]);

    ALTER TABLE [{Schema}].[SqlOSEmailOtpChallenges]
        ADD CONSTRAINT [FK_SqlOSEmailOtpChallenges_ClientApplications_ClientApplicationId]
            FOREIGN KEY ([ClientApplicationId]) REFERENCES [{Schema}].[SqlOSClientApplications]([Id]);
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (12);
