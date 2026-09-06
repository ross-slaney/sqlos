IF OBJECT_ID(N'[{Schema}].[SqlOSApplicationAssignments]', N'U') IS NOT NULL AND COL_LENGTH('[{Schema}].[SqlOSApplicationAssignments]', 'ConfigurationOwner') IS NULL
BEGIN
    EXEC(N'ALTER TABLE [{Schema}].[SqlOSApplicationAssignments] ADD [ConfigurationOwner] NVARCHAR(40) NOT NULL CONSTRAINT [DF_SqlOSApplicationAssignments_ConfigurationOwner] DEFAULT N''dashboard'', [ConfigurationSourceKey] NVARCHAR(160) NULL, [ConfigurationFingerprint] NVARCHAR(64) NULL, [LastReconciledAt] DATETIME2 NULL, [ConfigurationOrphanedAt] DATETIME2 NULL;');
END
IF OBJECT_ID(N'[{Schema}].[SqlOSApplicationAssignments]', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{Schema}].[SqlOSApplicationAssignments]') AND [name] = N'UX_SqlOSApplicationAssignments_Client_Owner_SourceKey')
    EXEC(N'CREATE UNIQUE INDEX [UX_SqlOSApplicationAssignments_Client_Owner_SourceKey] ON [{Schema}].[SqlOSApplicationAssignments]([ClientApplicationId], [ConfigurationOwner], [ConfigurationSourceKey]) WHERE [ConfigurationSourceKey] IS NOT NULL;');
