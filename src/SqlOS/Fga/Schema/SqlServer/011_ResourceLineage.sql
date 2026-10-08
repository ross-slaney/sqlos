-- SqlOSFga Schema v11: resource sequence numbers and the resource lineage.
--
-- Resources.Seq numbers resources in creation order; it is the compact key the lineage columns hold.
-- ResourceTypes.Seq is the compact key of a resource's type, copied to application tables that carry
-- scope columns.
--
-- The lineage of a resource is its ancestor at every level of the tree (Ancestor0 ... AncestorD, one
-- column per level: the default depth's here, deeper ones added by SqlOSFgaFunctionInitializer when
-- MaxResourceHierarchyDepth exceeds 10), its Depth, and its Reach: the highest level from which access flows down
-- to it without crossing an inactive resource. An inactive resource, or one in a cycle or deeper than the
-- limit, has no Reach and no Depth, which denies it exactly as fn_IsResourceAccessible always has.
-- Triggers created by SqlOSFgaFunctionInitializer keep the columns exact; it also fills them once after
-- this migration.

-- 1. ResourceTypes.Seq
IF COL_LENGTH('[{Schema}].[{ResourceTypes}]', 'Seq') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{ResourceTypes}] ADD [Seq] INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.sequences WHERE name = '{ResourceTypes}_Seq' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    EXEC('CREATE SEQUENCE [{Schema}].[{ResourceTypes}_Seq] AS INT START WITH 1 INCREMENT BY 1');
END
GO

;WITH numbered AS (
    SELECT [Seq], ROW_NUMBER() OVER (ORDER BY [Id]) AS rn
    FROM [{Schema}].[{ResourceTypes}]
    WHERE [Seq] IS NULL
)
UPDATE numbered
SET [Seq] = rn + ISNULL((SELECT MAX([Seq]) FROM [{Schema}].[{ResourceTypes}]), 0);
GO

DECLARE @typeRestart NVARCHAR(32) = CAST(ISNULL((SELECT MAX([Seq]) FROM [{Schema}].[{ResourceTypes}]), 0) + 1 AS NVARCHAR(32));
EXEC('ALTER SEQUENCE [{Schema}].[{ResourceTypes}_Seq] RESTART WITH ' + @typeRestart);
GO

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('[{Schema}].[{ResourceTypes}]') AND name = 'Seq' AND is_nullable = 1
)
BEGIN
    ALTER TABLE [{Schema}].[{ResourceTypes}] ALTER COLUMN [Seq] INT NOT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_{ResourceTypes}_Seq' AND parent_object_id = OBJECT_ID('[{Schema}].[{ResourceTypes}]'))
BEGIN
    ALTER TABLE [{Schema}].[{ResourceTypes}]
        ADD CONSTRAINT [DF_{ResourceTypes}_Seq] DEFAULT (NEXT VALUE FOR [{Schema}].[{ResourceTypes}_Seq]) FOR [Seq];
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_{ResourceTypes}_Seq' AND object_id = OBJECT_ID('[{Schema}].[{ResourceTypes}]'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_{ResourceTypes}_Seq] ON [{Schema}].[{ResourceTypes}]([Seq]);
END
GO

-- 2. Resources.Seq
IF COL_LENGTH('[{Schema}].[{Resources}]', 'Seq') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{Resources}] ADD [Seq] BIGINT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.sequences WHERE name = '{Resources}_Seq' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    EXEC('CREATE SEQUENCE [{Schema}].[{Resources}_Seq] AS BIGINT START WITH 1 INCREMENT BY 1 CACHE 1000');
END
GO

;WITH numbered AS (
    SELECT [Seq], ROW_NUMBER() OVER (ORDER BY [CreatedAt], [Id]) AS rn
    FROM [{Schema}].[{Resources}]
    WHERE [Seq] IS NULL
)
UPDATE numbered
SET [Seq] = rn + ISNULL((SELECT MAX([Seq]) FROM [{Schema}].[{Resources}]), 0);
GO

DECLARE @resourceRestart NVARCHAR(32) = CAST(ISNULL((SELECT MAX([Seq]) FROM [{Schema}].[{Resources}]), 0) + 1 AS NVARCHAR(32));
EXEC('ALTER SEQUENCE [{Schema}].[{Resources}_Seq] RESTART WITH ' + @resourceRestart);
GO

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('[{Schema}].[{Resources}]') AND name = 'Seq' AND is_nullable = 1
)
BEGIN
    ALTER TABLE [{Schema}].[{Resources}] ALTER COLUMN [Seq] BIGINT NOT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_{Resources}_Seq' AND parent_object_id = OBJECT_ID('[{Schema}].[{Resources}]'))
BEGIN
    ALTER TABLE [{Schema}].[{Resources}]
        ADD CONSTRAINT [DF_{Resources}_Seq] DEFAULT (NEXT VALUE FOR [{Schema}].[{Resources}_Seq]) FOR [Seq];
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_{Resources}_Seq' AND object_id = OBJECT_ID('[{Schema}].[{Resources}]'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_{Resources}_Seq] ON [{Schema}].[{Resources}]([Seq]);
END
GO

-- 3. Depth and Reach.
IF COL_LENGTH('[{Schema}].[{Resources}]', 'Depth') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{Resources}] ADD [Depth] SMALLINT NULL;
END
GO

IF COL_LENGTH('[{Schema}].[{Resources}]', 'Reach') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{Resources}] ADD [Reach] SMALLINT NULL;
END
GO

-- 4. The ancestor columns of the default depth (levels 0..10). A larger configured depth adds the columns
--    above level 10 through SqlOSFgaFunctionInitializer. They are read by resource id or by Seq, never
--    searched by value, so they carry no index.
IF COL_LENGTH('[{Schema}].[{Resources}]', 'Ancestor0') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{Resources}] ADD [Ancestor0] BIGINT NULL;
END
GO

IF COL_LENGTH('[{Schema}].[{Resources}]', 'Ancestor1') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{Resources}] ADD [Ancestor1] BIGINT NULL;
END
GO

IF COL_LENGTH('[{Schema}].[{Resources}]', 'Ancestor2') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{Resources}] ADD [Ancestor2] BIGINT NULL;
END
GO

IF COL_LENGTH('[{Schema}].[{Resources}]', 'Ancestor3') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{Resources}] ADD [Ancestor3] BIGINT NULL;
END
GO

IF COL_LENGTH('[{Schema}].[{Resources}]', 'Ancestor4') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{Resources}] ADD [Ancestor4] BIGINT NULL;
END
GO

IF COL_LENGTH('[{Schema}].[{Resources}]', 'Ancestor5') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{Resources}] ADD [Ancestor5] BIGINT NULL;
END
GO

IF COL_LENGTH('[{Schema}].[{Resources}]', 'Ancestor6') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{Resources}] ADD [Ancestor6] BIGINT NULL;
END
GO

IF COL_LENGTH('[{Schema}].[{Resources}]', 'Ancestor7') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{Resources}] ADD [Ancestor7] BIGINT NULL;
END
GO

IF COL_LENGTH('[{Schema}].[{Resources}]', 'Ancestor8') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{Resources}] ADD [Ancestor8] BIGINT NULL;
END
GO

IF COL_LENGTH('[{Schema}].[{Resources}]', 'Ancestor9') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{Resources}] ADD [Ancestor9] BIGINT NULL;
END
GO

IF COL_LENGTH('[{Schema}].[{Resources}]', 'Ancestor10') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{Resources}] ADD [Ancestor10] BIGINT NULL;
END
GO

-- 5. The hash of the enforcement routines (functions, lineage procedures, triggers) last applied by
--    SqlOSFgaFunctionInitializer, so unchanged definitions are not re-created on every startup.
IF COL_LENGTH('[{Schema}].[SqlOSFgaSchema]', 'RoutinesHash') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSFgaSchema] ADD [RoutinesHash] NVARCHAR(64) NULL;
END
GO

UPDATE [{Schema}].[SqlOSFgaSchema] SET [Version] = 11 WHERE [Version] < 11;
GO
