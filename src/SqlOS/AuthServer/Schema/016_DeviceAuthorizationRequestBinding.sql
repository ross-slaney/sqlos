IF COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'DeviceAuthorizationId') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ADD [DeviceAuthorizationId] NVARCHAR(64) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'DeviceAuthorizationId') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.foreign_keys
       WHERE [name] = 'FK_SqlOSAuthorizationRequests_DeviceAuthorization'
         AND [parent_object_id] = OBJECT_ID('{Schema}.SqlOSAuthorizationRequests')
   )
   AND OBJECT_ID('{Schema}.SqlOSDeviceAuthorizations') IS NOT NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ADD CONSTRAINT [FK_SqlOSAuthorizationRequests_DeviceAuthorization]
    FOREIGN KEY ([DeviceAuthorizationId]) REFERENCES [{Schema}].[SqlOSDeviceAuthorizations]([Id]);
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

IF COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'DeviceAuthorizationId') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes
       WHERE name = 'IX_SqlOSAuthorizationRequests_DeviceAuthorizationId'
         AND object_id = OBJECT_ID('{Schema}.SqlOSAuthorizationRequests')
   )
BEGIN
    CREATE UNIQUE INDEX [IX_SqlOSAuthorizationRequests_DeviceAuthorizationId]
    ON [{Schema}].[SqlOSAuthorizationRequests]([DeviceAuthorizationId])
    WHERE [DeviceAuthorizationId] IS NOT NULL;
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (16);
