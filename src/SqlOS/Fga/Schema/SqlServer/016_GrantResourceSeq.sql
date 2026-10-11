-- SqlOSFga Schema v16: find one subject's grants on one resource with one seek.
--
-- The point check (fn_IsResourceAccessible, and the list filter that checks each row with it) looks, on each
-- ancestor of a resource, for a grant to one of the caller's live subjects. Keyed on the resource, that lookup
-- reads every grant on the ancestor, whoever holds it: on a node where every member of an organization holds a
-- grant, every check pays for all of them. A key on (ResourceId, SubjectId) would seek, but two NVARCHAR(450)
-- keys exceed SQL Server's 1,700-byte limit (schema v10).
--
-- ResourceSeq is the grant's copy of its resource's Seq (schema v11), which never changes. (ResourceSeq,
-- SubjectId) is 908 bytes, and the point check reads the ancestors' Seqs from the lineage, so it seeks the
-- caller's grants on each ancestor exactly. A trigger (SqlOSFgaFunctionInitializer) fills the column when a
-- grant is written; the lineage rebuild refreshes it.

IF COL_LENGTH('[{Schema}].[{Grants}]', 'ResourceSeq') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{Grants}] ADD [ResourceSeq] BIGINT NULL;
END
GO

UPDATE g SET [ResourceSeq] = r.[Seq]
FROM [{Schema}].[{Grants}] g
INNER JOIN [{Schema}].[{Resources}] r ON r.[Id] = g.[ResourceId]
WHERE g.[ResourceSeq] IS NULL OR g.[ResourceSeq] <> r.[Seq];
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_{Grants}_ResourceSeq_SubjectId' AND object_id = OBJECT_ID('[{Schema}].[{Grants}]'))
BEGIN
    CREATE NONCLUSTERED INDEX [IX_{Grants}_ResourceSeq_SubjectId]
        ON [{Schema}].[{Grants}]([ResourceSeq], [SubjectId])
        INCLUDE ([Id], [ResourceId], [RoleId], [EffectiveFrom], [EffectiveTo]);
END
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_{Grants}_ResourceId_SubjectId' AND object_id = OBJECT_ID('[{Schema}].[{Grants}]'))
BEGIN
    DROP INDEX [IX_{Grants}_ResourceId_SubjectId] ON [{Schema}].[{Grants}];
END
GO

UPDATE [{Schema}].[SqlOSFgaSchema] SET [Version] = 16 WHERE [Version] < 16;
GO
