-- PostgreSQL translation of the matching SQL Server script.
-- SqlOSFga Schema v12: the application tables this installation protects.
--
-- SqlOSFgaFunctionInitializer records here the tables it put scope triggers, indexes and statistics on. At
-- startup it removes SqlOS's objects only from tables this installation owns (the ones recorded, and the
-- ones the model protects now), so another SqlOS installation sharing the database, in its own schema, keeps
-- its objects.

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSFgaScopeTables" (
    "TableSchema" varchar(128) NOT NULL,
    "TableName"   varchar(128) NOT NULL,
    PRIMARY KEY ("TableSchema", "TableName")
);

UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 12 WHERE "Version" < 12;
