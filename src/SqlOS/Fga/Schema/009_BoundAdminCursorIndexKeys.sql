-- SqlOSFga Schema v9: keep admin cursor index keys under SQL Server's 1,700-byte limit.
-- v8 keyed two NVARCHAR(450) columns (1,800 bytes). Recreate those indexes with the
-- unique Id tiebreaker INCLUDE'd so max-length resource and grant writes stay valid.

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_{Resources}_ParentId_Id'
      AND object_id = OBJECT_ID('{Schema}.{Resources}')
)
BEGIN
    DROP INDEX [IX_{Resources}_ParentId_Id] ON [{Schema}].[{Resources}];
END
GO

CREATE NONCLUSTERED INDEX [IX_{Resources}_ParentId_Id]
    ON [{Schema}].[{Resources}]([ParentId])
    INCLUDE ([Id]);
GO

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_{Grants}_SubjectId_CreatedAt_Id'
      AND object_id = OBJECT_ID('{Schema}.{Grants}')
)
BEGIN
    DROP INDEX [IX_{Grants}_SubjectId_CreatedAt_Id] ON [{Schema}].[{Grants}];
END
GO

CREATE NONCLUSTERED INDEX [IX_{Grants}_SubjectId_CreatedAt_Id]
    ON [{Schema}].[{Grants}]([SubjectId], [CreatedAt] DESC)
    INCLUDE ([Id]);
GO

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_{Grants}_ResourceId_CreatedAt_Id'
      AND object_id = OBJECT_ID('{Schema}.{Grants}')
)
BEGIN
    DROP INDEX [IX_{Grants}_ResourceId_CreatedAt_Id] ON [{Schema}].[{Grants}];
END
GO

CREATE NONCLUSTERED INDEX [IX_{Grants}_ResourceId_CreatedAt_Id]
    ON [{Schema}].[{Grants}]([ResourceId], [CreatedAt] DESC)
    INCLUDE ([Id]);
GO

UPDATE [{Schema}].[SqlOSFgaSchema] SET [Version] = 9 WHERE [Version] < 9;
GO
