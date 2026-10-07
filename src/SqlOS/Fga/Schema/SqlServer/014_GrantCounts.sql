-- SqlOSFga Schema v14: the grant counts behind authorized pages.
--
-- Per (principal, resource) — the resource and every ancestor of a resource the principal holds a grant on —
-- the principal's usable grants at or below the resource, the children that hold such grants, and the grants
-- below an inactive descendant. The page executor reads them to decide at every node whether to stream the
-- node's rows or jump to its children, so a page costs about its own size rather than the table's.
-- Routines and triggers created by SqlOSFgaFunctionInitializer keep the counts exact and rebuild them when
-- the lineage is rebuilt; the direct indexes of application tables are created by the initializer as well.

IF OBJECT_ID(N'[{Schema}].[SqlOSFgaGrantCounts]', N'U') IS NULL
BEGIN
    CREATE TABLE [{Schema}].[SqlOSFgaGrantCounts] (
        [SubjectId] NVARCHAR(450) NOT NULL,
        [ResourceSeq] BIGINT NOT NULL,
        [ParentSeq] BIGINT NULL,
        [Grants] INT NOT NULL CONSTRAINT [DF_SqlOSFgaGrantCounts_Grants] DEFAULT 0,
        [GrantChildren] INT NOT NULL CONSTRAINT [DF_SqlOSFgaGrantCounts_GrantChildren] DEFAULT 0,
        [CutGrants] INT NOT NULL CONSTRAINT [DF_SqlOSFgaGrantCounts_CutGrants] DEFAULT 0,
        CONSTRAINT [PK_SqlOSFgaGrantCounts] PRIMARY KEY ([SubjectId], [ResourceSeq])
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SqlOSFgaGrantCounts_Parent' AND object_id = OBJECT_ID(N'[{Schema}].[SqlOSFgaGrantCounts]'))
    CREATE NONCLUSTERED INDEX [IX_SqlOSFgaGrantCounts_Parent] ON [{Schema}].[SqlOSFgaGrantCounts] ([SubjectId], [ParentSeq], [ResourceSeq]) INCLUDE ([Grants], [GrantChildren], [CutGrants]);
GO

UPDATE [{Schema}].[SqlOSFgaSchema] SET [Version] = 14 WHERE [Version] < 14;
GO
