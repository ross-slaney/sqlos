-- SqlOSFga Schema v11: resource sequence numbers and the ancestor closure.
--
-- Resources.Seq numbers resources in creation order. It is the cursor key for authorized pages and the
-- compact key of the closure. ResourceTypes.Seq keys the closure by the descendant's type, so a page of
-- one entity type under one ancestor is a single index range.
--
-- The closure holds one row per (proper ancestor, descendant) pair whose path is fully active. The triggers
-- that maintain it, and the procedure that rebuilds it, are created by SqlOSFgaFunctionInitializer, which
-- also fills it once after this migration.

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

-- 3. The closure
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = '{Resources}Closure' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[{Resources}Closure] (
        [AncestorSeq]   BIGINT NOT NULL,
        [TypeSeq]       INT    NOT NULL,
        [DescendantSeq] BIGINT NOT NULL,
        CONSTRAINT [PK_{Resources}Closure] PRIMARY KEY CLUSTERED ([AncestorSeq], [TypeSeq], [DescendantSeq])
    );
END
GO

-- 4. The hash of the enforcement routines (functions, closure procedures, triggers) last applied by
--    SqlOSFgaFunctionInitializer, so unchanged definitions are not re-created on every startup.
IF COL_LENGTH('[{Schema}].[SqlOSFgaSchema]', 'RoutinesHash') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSFgaSchema] ADD [RoutinesHash] NVARCHAR(64) NULL;
END
GO

UPDATE [{Schema}].[SqlOSFgaSchema] SET [Version] = 11 WHERE [Version] < 11;
GO
