IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSUserPhoneNumbers' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSUserPhoneNumbers] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [UserId] NVARCHAR(64) NOT NULL,
        [PhoneNumber] NVARCHAR(32) NOT NULL,
        [PhoneNumberHash] NVARCHAR(128) NOT NULL,
        [DisplayValueEncrypted] NVARCHAR(2048) NULL,
        [IsPrimary] BIT NOT NULL CONSTRAINT [DF_SqlOSUserPhoneNumbers_IsPrimary] DEFAULT 0,
        [IsVerified] BIT NOT NULL CONSTRAINT [DF_SqlOSUserPhoneNumbers_IsVerified] DEFAULT 0,
        [VerifiedAt] DATETIME2 NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL,
        [LastUsedAt] DATETIME2 NULL,
        [RemovedAt] DATETIME2 NULL,
        [RemovalReason] NVARCHAR(120) NULL
    );

    CREATE UNIQUE INDEX [IX_SqlOSUserPhoneNumbers_PhoneNumberHash]
        ON [{Schema}].[SqlOSUserPhoneNumbers]([PhoneNumberHash])
        WHERE [RemovedAt] IS NULL;

    CREATE INDEX [IX_SqlOSUserPhoneNumbers_UserId_RemovedAt]
        ON [{Schema}].[SqlOSUserPhoneNumbers]([UserId], [RemovedAt]);

    ALTER TABLE [{Schema}].[SqlOSUserPhoneNumbers]
        ADD CONSTRAINT [FK_SqlOSUserPhoneNumbers_Users_UserId]
            FOREIGN KEY ([UserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id]);
END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSPhoneOtpChallenges' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSPhoneOtpChallenges] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [ChallengeTokenHash] NVARCHAR(128) NOT NULL,
        [PhoneNumberHash] NVARCHAR(128) NOT NULL,
        [PhoneNumberEncrypted] NVARCHAR(2048) NOT NULL,
        [MaskedPhoneNumber] NVARCHAR(32) NOT NULL,
        [Purpose] NVARCHAR(32) NOT NULL,
        [UserId] NVARCHAR(64) NULL,
        [UserPhoneNumberId] NVARCHAR(64) NULL,
        [AuthorizationRequestId] NVARCHAR(64) NULL,
        [ClientApplicationId] NVARCHAR(64) NULL,
        [RequestedOrganizationId] NVARCHAR(64) NULL,
        [ProviderStarted] BIT NOT NULL CONSTRAINT [DF_SqlOSPhoneOtpChallenges_ProviderStarted] DEFAULT 0,
        [Provider] NVARCHAR(40) NOT NULL,
        [ProviderChallengeId] NVARCHAR(128) NULL,
        [ProviderStatus] NVARCHAR(80) NULL,
        [AttemptCount] INT NOT NULL CONSTRAINT [DF_SqlOSPhoneOtpChallenges_AttemptCount] DEFAULT 0,
        [CreatedAt] DATETIME2 NOT NULL,
        [ExpiresAt] DATETIME2 NOT NULL,
        [LastSentAt] DATETIME2 NOT NULL,
        [ConsumedAt] DATETIME2 NULL,
        [InvalidatedAt] DATETIME2 NULL,
        [InvalidatedReason] NVARCHAR(120) NULL,
        [IpAddress] NVARCHAR(128) NULL,
        [UserAgent] NVARCHAR(512) NULL
    );

    CREATE UNIQUE INDEX [IX_SqlOSPhoneOtpChallenges_ChallengeTokenHash]
        ON [{Schema}].[SqlOSPhoneOtpChallenges]([ChallengeTokenHash]);

    CREATE INDEX [IX_SqlOSPhoneOtpChallenges_PhoneNumberHash_CreatedAt]
        ON [{Schema}].[SqlOSPhoneOtpChallenges]([PhoneNumberHash], [CreatedAt]);

    CREATE INDEX [IX_SqlOSPhoneOtpChallenges_UserId_CreatedAt]
        ON [{Schema}].[SqlOSPhoneOtpChallenges]([UserId], [CreatedAt]);

    CREATE INDEX [IX_SqlOSPhoneOtpChallenges_IpAddress_CreatedAt]
        ON [{Schema}].[SqlOSPhoneOtpChallenges]([IpAddress], [CreatedAt]);

    CREATE INDEX [IX_SqlOSPhoneOtpChallenges_ClientApplicationId_CreatedAt]
        ON [{Schema}].[SqlOSPhoneOtpChallenges]([ClientApplicationId], [CreatedAt]);

    ALTER TABLE [{Schema}].[SqlOSPhoneOtpChallenges]
        ADD CONSTRAINT [FK_SqlOSPhoneOtpChallenges_Users_UserId]
            FOREIGN KEY ([UserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id]);

    ALTER TABLE [{Schema}].[SqlOSPhoneOtpChallenges]
        ADD CONSTRAINT [FK_SqlOSPhoneOtpChallenges_UserPhoneNumbers_UserPhoneNumberId]
            FOREIGN KEY ([UserPhoneNumberId]) REFERENCES [{Schema}].[SqlOSUserPhoneNumbers]([Id]);

    ALTER TABLE [{Schema}].[SqlOSPhoneOtpChallenges]
        ADD CONSTRAINT [FK_SqlOSPhoneOtpChallenges_AuthorizationRequests_AuthorizationRequestId]
            FOREIGN KEY ([AuthorizationRequestId]) REFERENCES [{Schema}].[SqlOSAuthorizationRequests]([Id]);

    ALTER TABLE [{Schema}].[SqlOSPhoneOtpChallenges]
        ADD CONSTRAINT [FK_SqlOSPhoneOtpChallenges_ClientApplications_ClientApplicationId]
            FOREIGN KEY ([ClientApplicationId]) REFERENCES [{Schema}].[SqlOSClientApplications]([Id]);
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (18);
