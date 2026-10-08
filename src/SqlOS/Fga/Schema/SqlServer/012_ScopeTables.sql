-- SqlOSFga Schema v12: the application tables this installation protects.
--
-- SqlOSFgaFunctionInitializer records here the tables it put scope triggers, indexes, statistics and
-- computed columns on. At startup it removes SqlOS's objects only from tables this installation owns (the
-- ones recorded, and the ones the model protects now), so another SqlOS installation sharing the database,
-- in its own schema, keeps its objects.

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SqlOSFgaScopeTables' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSFgaScopeTables] (
        [TableSchema] NVARCHAR(128) NOT NULL,
        [TableName]   NVARCHAR(128) NOT NULL,
        CONSTRAINT [PK_SqlOSFgaScopeTables] PRIMARY KEY ([TableSchema], [TableName])
    );
END
GO

UPDATE [{Schema}].[SqlOSFgaSchema] SET [Version] = 12 WHERE [Version] < 12;
GO
