-- SqlOSFga Schema v13: whether the resource lineage has been built in full.
--
-- The rebuild commits in ranges, so a rebuild that fails part-way leaves some rows with a lineage and some
-- without. The rebuild clears this flag before its first range and sets it after its last, and startup
-- rebuilds while it is clear: the lineage is complete exactly when the flag says so.

IF COL_LENGTH('[{Schema}].[SqlOSFgaSchema]', 'LineageBuilt') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSFgaSchema] ADD [LineageBuilt] BIT NOT NULL CONSTRAINT [DF_SqlOSFgaSchema_LineageBuilt] DEFAULT 0;
END
GO

UPDATE [{Schema}].[SqlOSFgaSchema] SET [Version] = 13 WHERE [Version] < 13;
GO
