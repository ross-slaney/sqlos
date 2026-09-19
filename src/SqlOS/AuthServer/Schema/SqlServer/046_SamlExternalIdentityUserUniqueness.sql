-- One SAML connection may bind a given user to at most one IdP subject.
-- Duplicate rows from earlier email-link races keep the oldest binding.

IF OBJECT_ID(N'[{Schema}].[SqlOSExternalIdentities]', N'U') IS NOT NULL
   AND COL_LENGTH('[{Schema}].[SqlOSExternalIdentities]', 'ConnectionId') IS NOT NULL
BEGIN
    ;WITH ranked AS (
        SELECT [Id],
               ROW_NUMBER() OVER (
                   PARTITION BY [ConnectionId], [UserId]
                   ORDER BY [CreatedAt] ASC, [Id] ASC) AS [RowNumber]
        FROM [{Schema}].[SqlOSExternalIdentities]
        WHERE [ConnectionId] IS NOT NULL
    )
    DELETE FROM [{Schema}].[SqlOSExternalIdentities]
    WHERE [Id] IN (SELECT [Id] FROM ranked WHERE [RowNumber] > 1);

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE [name] = N'IX_SqlOSExternalIdentities_SsoConnectionId_UserId'
          AND [object_id] = OBJECT_ID(N'[{Schema}].[SqlOSExternalIdentities]'))
    BEGIN
        CREATE UNIQUE INDEX [IX_SqlOSExternalIdentities_SsoConnectionId_UserId]
            ON [{Schema}].[SqlOSExternalIdentities]([ConnectionId], [UserId])
            WHERE [ConnectionId] IS NOT NULL;
    END
END
