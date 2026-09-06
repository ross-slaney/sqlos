IF COL_LENGTH('{Schema}.{ServiceAccounts}', 'ConfigurationOwner') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{ServiceAccounts}] ADD
        [ConfigurationOwner] NVARCHAR(32) NOT NULL CONSTRAINT [DF_{ServiceAccounts}_ConfigurationOwner] DEFAULT N'dashboard',
        [ConfigurationSourceKey] NVARCHAR(200) NULL,
        [ConfigurationFingerprint] NVARCHAR(128) NULL,
        [LastReconciledAt] DATETIME2 NULL,
        [ConfigurationOrphanedAt] DATETIME2 NULL;
END
GO

ALTER TABLE [{Schema}].[{ServiceAccounts}] ALTER COLUMN [ClientId] NVARCHAR(450) NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_{ServiceAccounts}_ClientId' AND object_id = OBJECT_ID('[{Schema}].[{ServiceAccounts}]'))
    CREATE UNIQUE INDEX [UX_{ServiceAccounts}_ClientId] ON [{Schema}].[{ServiceAccounts}] ([ClientId]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_{ServiceAccounts}_ConfigurationSource' AND object_id = OBJECT_ID('[{Schema}].[{ServiceAccounts}]'))
    CREATE UNIQUE INDEX [UX_{ServiceAccounts}_ConfigurationSource] ON [{Schema}].[{ServiceAccounts}] ([ConfigurationOwner], [ConfigurationSourceKey]) WHERE [ConfigurationSourceKey] IS NOT NULL;
GO

UPDATE [{Schema}].[SqlOSFgaSchema] SET [Version] = 7 WHERE [Version] < 7;
GO
