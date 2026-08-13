-- SqlOSFga Schema v8: admin cursor-pagination indexes.
-- Name/key columns remain NVARCHAR(MAX) and cannot host a full keyset index.
-- Parent-scoped resource windows and time-ordered grants get unique-tiebreaker indexes.

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_{Resources}_ParentId_Id'
      AND object_id = OBJECT_ID('{Schema}.{Resources}')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{Resources}_ParentId_Id]
        ON [{Schema}].[{Resources}]([ParentId], [Id]);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_{Grants}_CreatedAt_Id'
      AND object_id = OBJECT_ID('{Schema}.{Grants}')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{Grants}_CreatedAt_Id]
        ON [{Schema}].[{Grants}]([CreatedAt] DESC, [Id] DESC);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_{Grants}_SubjectId_CreatedAt_Id'
      AND object_id = OBJECT_ID('{Schema}.{Grants}')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{Grants}_SubjectId_CreatedAt_Id]
        ON [{Schema}].[{Grants}]([SubjectId], [CreatedAt] DESC, [Id] DESC);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_{Grants}_ResourceId_CreatedAt_Id'
      AND object_id = OBJECT_ID('{Schema}.{Grants}')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{Grants}_ResourceId_CreatedAt_Id]
        ON [{Schema}].[{Grants}]([ResourceId], [CreatedAt] DESC, [Id] DESC);
END
GO

UPDATE [{Schema}].[SqlOSFgaSchema] SET [Version] = 8 WHERE [Version] < 8;
GO
