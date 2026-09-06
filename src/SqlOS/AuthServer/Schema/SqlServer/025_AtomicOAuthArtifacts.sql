IF OBJECT_ID('[{Schema}].[SqlOSAuthorizationCodes]', 'U') IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_SqlOSAuthorizationCodes_AuthorizationRequestId'
      AND object_id = OBJECT_ID('[{Schema}].[SqlOSAuthorizationCodes]')
)
BEGIN
    CREATE UNIQUE INDEX [IX_SqlOSAuthorizationCodes_AuthorizationRequestId]
    ON [{Schema}].[SqlOSAuthorizationCodes]([AuthorizationRequestId]);
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (25);
