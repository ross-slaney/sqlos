-- SqlOSFga Schema v16: find one subject's grants on one resource with one seek.
--
-- The point check (fn_IsResourceAccessible, and the list filter that checks each row with it) looks, on each
-- ancestor of a resource, for a grant to one of the caller's live subjects. Keyed on the resource, that lookup
-- reads every grant on the ancestor, whoever holds it: on a node where every member of an organization holds a
-- grant, every check pays for all of them. A key on (ResourceId, SubjectId) would seek, but two NVARCHAR(450)
-- keys exceed SQL Server's 1,700-byte limit (schema v10).
--
-- ResourceSubjectHash is CHECKSUM over the two ids: four bytes, and equal for ids the column's collation calls
-- equal (CHECKSUM's documented contract), so a grant whose ids differ from the caller's only in case is still
-- found. Different pairs may share a hash; the lookup compares the ids.
--
-- The grants by subject, which the access roots read, also carry what the roots need, so listing a caller's
-- grants costs no lookup per grant.

IF COL_LENGTH('[{Schema}].[{Grants}]', 'ResourceSubjectHash') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{Grants}] ADD [ResourceSubjectHash] AS CHECKSUM([ResourceId], [SubjectId]) PERSISTED;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_{Grants}_ResourceSubjectHash' AND object_id = OBJECT_ID('[{Schema}].[{Grants}]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{Grants}_ResourceSubjectHash]
        ON [{Schema}].[{Grants}]([ResourceSubjectHash])
        INCLUDE ([Id], [ResourceId], [SubjectId], [RoleId], [EffectiveFrom], [EffectiveTo]);
END
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_{Grants}_ResourceId_SubjectId' AND object_id = OBJECT_ID('[{Schema}].[{Grants}]'))
BEGIN
    DROP INDEX [IX_{Grants}_ResourceId_SubjectId] ON [{Schema}].[{Grants}];
END
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_{Grants}_SubjectId' AND object_id = OBJECT_ID('[{Schema}].[{Grants}]'))
BEGIN
    DROP INDEX [IX_{Grants}_SubjectId] ON [{Schema}].[{Grants}];
END
GO

CREATE NONCLUSTERED INDEX [IX_{Grants}_SubjectId]
    ON [{Schema}].[{Grants}]([SubjectId])
    INCLUDE ([ResourceId], [RoleId], [EffectiveFrom], [EffectiveTo]);
GO

UPDATE [{Schema}].[SqlOSFgaSchema] SET [Version] = 16 WHERE [Version] < 16;
GO
