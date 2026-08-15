-- SqlOSFga Schema v10: keep the v4 grant/role-permission lookup indexes under
-- SQL Server's 1,700-byte nonclustered key limit. Two NVARCHAR(450) key columns
-- are 1,800 bytes and reject otherwise-valid max-length identifiers.

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_{Grants}_ResourceId_SubjectId'
      AND object_id = OBJECT_ID('{Schema}.{Grants}')
)
BEGIN
    DROP INDEX [IX_{Grants}_ResourceId_SubjectId] ON [{Schema}].[{Grants}];
END
GO

CREATE NONCLUSTERED INDEX [IX_{Grants}_ResourceId_SubjectId]
    ON [{Schema}].[{Grants}]([ResourceId])
    INCLUDE ([SubjectId], [RoleId], [EffectiveFrom], [EffectiveTo]);
GO

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_{RolePermissions}_PermissionId_RoleId'
      AND object_id = OBJECT_ID('{Schema}.{RolePermissions}')
)
BEGIN
    DROP INDEX [IX_{RolePermissions}_PermissionId_RoleId] ON [{Schema}].[{RolePermissions}];
END
GO

CREATE NONCLUSTERED INDEX [IX_{RolePermissions}_PermissionId_RoleId]
    ON [{Schema}].[{RolePermissions}]([PermissionId])
    INCLUDE ([RoleId]);
GO

UPDATE [{Schema}].[SqlOSFgaSchema] SET [Version] = 10 WHERE [Version] < 10;
GO
