IF OBJECT_ID('[{Schema}].[SqlOSAuditEvents]', 'U') IS NOT NULL
AND COL_LENGTH('[{Schema}].[SqlOSAuditEvents]', 'IdempotencyScopeHash') IS NULL
    ALTER TABLE [{Schema}].[SqlOSAuditEvents] ADD [IdempotencyScopeHash] NVARCHAR(128) NULL;

GO

IF OBJECT_ID('[{Schema}].[SqlOSAuditEvents]', 'U') IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE [name] = 'UX_SqlOSAuditEvents_IdempotencyScopeHash'
      AND [object_id] = OBJECT_ID('[{Schema}].[SqlOSAuditEvents]')
)
BEGIN
    CREATE UNIQUE INDEX [UX_SqlOSAuditEvents_IdempotencyScopeHash]
        ON [{Schema}].[SqlOSAuditEvents] ([IdempotencyScopeHash])
        WHERE [IdempotencyScopeHash] IS NOT NULL;
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (38);
