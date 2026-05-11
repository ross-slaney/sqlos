IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'AllowDeviceAuthorization') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [AllowDeviceAuthorization] BIT NOT NULL
        CONSTRAINT [DF_SqlOSClientApplications_AllowDeviceAuthorization] DEFAULT 0;
END

GO

IF OBJECT_ID('{Schema}.SqlOSDeviceAuthorizations', 'U') IS NULL
BEGIN
    CREATE TABLE [{Schema}].[SqlOSDeviceAuthorizations]
    (
        [Id] NVARCHAR(64) NOT NULL,
        [DeviceCodeHash] NVARCHAR(512) NOT NULL,
        [UserCodeHash] NVARCHAR(512) NOT NULL,
        [UserCode] NVARCHAR(32) NOT NULL,
        [ClientApplicationId] NVARCHAR(64) NOT NULL,
        [Scope] NVARCHAR(1000) NOT NULL,
        [Resource] NVARCHAR(2048) NULL,
        [Status] NVARCHAR(32) NOT NULL,
        [PollingIntervalSeconds] INT NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [ExpiresAt] DATETIME2 NOT NULL,
        [LastPolledAt] DATETIME2 NULL,
        [PollCount] INT NOT NULL,
        [SlowDownCount] INT NOT NULL,
        [ApprovedUserId] NVARCHAR(64) NULL,
        [ApprovedOrganizationId] NVARCHAR(64) NULL,
        [AuthenticationMethod] NVARCHAR(50) NULL,
        [ApprovedAt] DATETIME2 NULL,
        [DeniedAt] DATETIME2 NULL,
        [ConsumedAt] DATETIME2 NULL,
        [IpAddress] NVARCHAR(64) NULL,
        [UserAgent] NVARCHAR(500) NULL,
        CONSTRAINT [PK_SqlOSDeviceAuthorizations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SqlOSDeviceAuthorizations_ClientApplication] FOREIGN KEY ([ClientApplicationId]) REFERENCES [{Schema}].[SqlOSClientApplications]([Id]),
        CONSTRAINT [FK_SqlOSDeviceAuthorizations_User] FOREIGN KEY ([ApprovedUserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id]),
        CONSTRAINT [FK_SqlOSDeviceAuthorizations_Organization] FOREIGN KEY ([ApprovedOrganizationId]) REFERENCES [{Schema}].[SqlOSOrganizations]([Id])
    );
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'DeviceAuthorizationId') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ADD [DeviceAuthorizationId] NVARCHAR(64) NULL;
END

GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = 'FK_SqlOSAuthorizationRequests_DeviceAuthorization'
      AND parent_object_id = OBJECT_ID('{Schema}.SqlOSAuthorizationRequests'))
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ADD CONSTRAINT [FK_SqlOSAuthorizationRequests_DeviceAuthorization]
    FOREIGN KEY ([DeviceAuthorizationId]) REFERENCES [{Schema}].[SqlOSDeviceAuthorizations]([Id]);
END

GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSDeviceAuthorizations_DeviceCodeHash' AND object_id = OBJECT_ID('{Schema}.SqlOSDeviceAuthorizations'))
BEGIN
    CREATE UNIQUE INDEX [IX_SqlOSDeviceAuthorizations_DeviceCodeHash]
    ON [{Schema}].[SqlOSDeviceAuthorizations]([DeviceCodeHash]);
END

GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSDeviceAuthorizations_UserCodeHash' AND object_id = OBJECT_ID('{Schema}.SqlOSDeviceAuthorizations'))
BEGIN
    CREATE UNIQUE INDEX [IX_SqlOSDeviceAuthorizations_UserCodeHash]
    ON [{Schema}].[SqlOSDeviceAuthorizations]([UserCodeHash]);
END

GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSDeviceAuthorizations_ClientCreatedAt' AND object_id = OBJECT_ID('{Schema}.SqlOSDeviceAuthorizations'))
BEGIN
    CREATE INDEX [IX_SqlOSDeviceAuthorizations_ClientCreatedAt]
    ON [{Schema}].[SqlOSDeviceAuthorizations]([ClientApplicationId], [CreatedAt]);
END

GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSDeviceAuthorizations_ClientStatusExpiresAt' AND object_id = OBJECT_ID('{Schema}.SqlOSDeviceAuthorizations'))
BEGIN
    CREATE INDEX [IX_SqlOSDeviceAuthorizations_ClientStatusExpiresAt]
    ON [{Schema}].[SqlOSDeviceAuthorizations]([ClientApplicationId], [Status], [ExpiresAt]);
END

GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSDeviceAuthorizations_IpCreatedAt' AND object_id = OBJECT_ID('{Schema}.SqlOSDeviceAuthorizations'))
BEGIN
    CREATE INDEX [IX_SqlOSDeviceAuthorizations_IpCreatedAt]
    ON [{Schema}].[SqlOSDeviceAuthorizations]([IpAddress], [CreatedAt]);
END

GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSDeviceAuthorizations_ExpiresAt' AND object_id = OBJECT_ID('{Schema}.SqlOSDeviceAuthorizations'))
BEGIN
    CREATE INDEX [IX_SqlOSDeviceAuthorizations_ExpiresAt]
    ON [{Schema}].[SqlOSDeviceAuthorizations]([ExpiresAt]);
END

GO

IF EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_SqlOSAuthorizationRequests_DeviceAuthorizationId'
      AND object_id = OBJECT_ID('{Schema}.SqlOSAuthorizationRequests')
      AND is_unique = 0
)
BEGIN
    DROP INDEX [IX_SqlOSAuthorizationRequests_DeviceAuthorizationId]
    ON [{Schema}].[SqlOSAuthorizationRequests];
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSAuthorizationRequests_DeviceAuthorizationId' AND object_id = OBJECT_ID('{Schema}.SqlOSAuthorizationRequests'))
BEGIN
    CREATE UNIQUE INDEX [IX_SqlOSAuthorizationRequests_DeviceAuthorizationId]
    ON [{Schema}].[SqlOSAuthorizationRequests]([DeviceAuthorizationId])
    WHERE [DeviceAuthorizationId] IS NOT NULL;
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (15);
