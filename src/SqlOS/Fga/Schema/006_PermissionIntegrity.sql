IF EXISTS (
    SELECT [Key]
    FROM [{Schema}].[{Permissions}]
    GROUP BY [Key]
    HAVING COUNT(*) > 1
)
BEGIN
    THROW 51000, 'SqlOS cannot enforce unique FGA permission keys because duplicate keys already exist. Remove or rename duplicate permissions and restart.', 1;
END
GO

IF EXISTS (
    SELECT 1
    FROM [{Schema}].[{Permissions}]
    WHERE LEN([Key]) > 450
)
BEGIN
    THROW 51001, 'SqlOS cannot index FGA permission keys longer than 450 characters. Shorten those permission keys and restart.', 1;
END
GO

ALTER TABLE [{Schema}].[{Permissions}]
ALTER COLUMN [Key] NVARCHAR(450) NOT NULL;
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'UX_{Permissions}_Key'
      AND object_id = OBJECT_ID('[{Schema}].[{Permissions}]')
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_{Permissions}_Key]
        ON [{Schema}].[{Permissions}]([Key]);
END
GO

UPDATE [{Schema}].[SqlOSFgaSchema] SET [Version] = 6 WHERE [Version] < 6;
GO
