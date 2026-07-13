-- SqlOSFga Schema v5: explicit group lifecycle used by authorization enforcement.

IF COL_LENGTH('[{Schema}].[{UserGroups}]', 'IsActive') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{UserGroups}]
        ADD [IsActive] BIT NOT NULL
            CONSTRAINT [DF_{UserGroups}_IsActive] DEFAULT 1 WITH VALUES;
END
GO

UPDATE [{Schema}].[SqlOSFgaSchema] SET [Version] = 5 WHERE [Version] < 5;
