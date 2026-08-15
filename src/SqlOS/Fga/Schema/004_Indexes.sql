-- SqlOSFga Schema v4: Query-composable authorization indexes
-- These indexes support the TVF plan used for resource ancestry walks and grant lookups.

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_{Resources}_ParentId'
      AND object_id = OBJECT_ID('{Schema}.{Resources}')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{Resources}_ParentId]
    ON [{Schema}].[{Resources}]([ParentId]);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_{RolePermissions}_PermissionId_RoleId'
      AND object_id = OBJECT_ID('{Schema}.{RolePermissions}')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{RolePermissions}_PermissionId_RoleId]
    ON [{Schema}].[{RolePermissions}]([PermissionId])
    INCLUDE ([RoleId]);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_{Grants}_ResourceId_SubjectId'
      AND object_id = OBJECT_ID('{Schema}.{Grants}')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{Grants}_ResourceId_SubjectId]
    ON [{Schema}].[{Grants}]([ResourceId])
    INCLUDE ([SubjectId], [RoleId], [EffectiveFrom], [EffectiveTo]);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_{Grants}_SubjectId'
      AND object_id = OBJECT_ID('{Schema}.{Grants}')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{Grants}_SubjectId]
    ON [{Schema}].[{Grants}]([SubjectId]);
END
GO

UPDATE [{Schema}].[SqlOSFgaSchema] SET [Version] = 4 WHERE [Version] < 4;
GO
