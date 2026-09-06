IF OBJECT_ID(N'[{Schema}].[SqlOSConsentGrants]', N'U') IS NULL
   AND OBJECT_ID(N'[{Schema}].[SqlOSUsers]', N'U') IS NOT NULL
   AND OBJECT_ID(N'[{Schema}].[SqlOSClientApplications]', N'U') IS NOT NULL
BEGIN
    CREATE TABLE [{Schema}].[SqlOSConsentGrants]
    (
        [Id] NVARCHAR(64) NOT NULL,
        [UserId] NVARCHAR(64) NOT NULL,
        [ClientApplicationId] NVARCHAR(64) NOT NULL,
        -- Approvals union scopes across requests, so the stored scope can grow well past a
        -- single request's scope; SqlOSConsentService guards the 4000-char ceiling before save.
        [Scope] NVARCHAR(4000) NOT NULL CONSTRAINT [DF_SqlOSConsentGrants_Scope] DEFAULT (''),
        [GrantedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL,
        [RevokedAt] DATETIME2 NULL,
        [RevocationReason] NVARCHAR(200) NULL,
        -- Fingerprint of the client's security-sensitive metadata the approval was granted
        -- against. Coverage checks reject a grant whose stored fingerprint no longer matches
        -- the client's current metadata, closing the approve/CIMD-refresh race; NULL is
        -- accepted as legacy (pre-fingerprint) data.
        [ClientMetadataFingerprint] NVARCHAR(64) NULL,
        CONSTRAINT [PK_SqlOSConsentGrants] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SqlOSConsentGrants_User] FOREIGN KEY ([UserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id]),
        CONSTRAINT [FK_SqlOSConsentGrants_ClientApplication] FOREIGN KEY ([ClientApplicationId]) REFERENCES [{Schema}].[SqlOSClientApplications]([Id])
    );

    CREATE UNIQUE INDEX [UX_SqlOSConsentGrants_ActiveUserClient]
        ON [{Schema}].[SqlOSConsentGrants]([UserId], [ClientApplicationId])
        WHERE [RevokedAt] IS NULL;
    CREATE INDEX [IX_SqlOSConsentGrants_ClientApplicationId]
        ON [{Schema}].[SqlOSConsentGrants]([ClientApplicationId]);
END

GO

IF OBJECT_ID(N'[{Schema}].[SqlOSScopeDisplayNames]', N'U') IS NULL
BEGIN
    CREATE TABLE [{Schema}].[SqlOSScopeDisplayNames]
    (
        [Id] NVARCHAR(64) NOT NULL,
        -- Binary collation so the unique Scope key distinguishes scopes the way scope
        -- policy compares them (ordinal); case-insensitive server collations would
        -- conflate e.g. 'files.read' and 'FILES.READ'.
        [Scope] NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [DisplayName] NVARCHAR(200) NOT NULL,
        [Description] NVARCHAR(1000) NULL,
        [ConfigurationOwner] NVARCHAR(40) NOT NULL CONSTRAINT [DF_SqlOSScopeDisplayNames_ConfigurationOwner] DEFAULT ('dashboard'),
        -- The scope string is the configuration source key, so it must fit any Scope value
        -- and share Scope's binary collation: the orphan-sweep SQL compares it against the
        -- in-memory seed set ordinally.
        [ConfigurationSourceKey] NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NULL,
        [ConfigurationFingerprint] NVARCHAR(64) NULL,
        [LastReconciledAt] DATETIME2 NULL,
        [ConfigurationOrphanedAt] DATETIME2 NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL,
        CONSTRAINT [PK_SqlOSScopeDisplayNames] PRIMARY KEY ([Id])
    );

    CREATE UNIQUE INDEX [UX_SqlOSScopeDisplayNames_Scope]
        ON [{Schema}].[SqlOSScopeDisplayNames]([Scope]);
END

GO

-- Binds a pending consent interstitial to the user who reached it. Reload re-minting
-- must not follow the browser's mutable auth-page session cookie to a different user.
IF OBJECT_ID(N'[{Schema}].[SqlOSAuthorizationRequests]', N'U') IS NOT NULL AND COL_LENGTH('[{Schema}].[SqlOSAuthorizationRequests]', 'PendingConsentUserId') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ADD [PendingConsentUserId] NVARCHAR(64) NULL;
END
