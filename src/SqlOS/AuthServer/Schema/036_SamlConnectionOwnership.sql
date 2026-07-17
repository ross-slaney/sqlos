IF OBJECT_ID(N'[{Schema}].[SqlOSSsoConnections]', N'U') IS NOT NULL AND COL_LENGTH('[{Schema}].[SqlOSSsoConnections]', 'ConfigurationOwner') IS NULL
BEGIN
    EXEC(N'ALTER TABLE [{Schema}].[SqlOSSsoConnections] ADD [ConfigurationOwner] NVARCHAR(40) NOT NULL CONSTRAINT [DF_SqlOSSsoConnections_ConfigurationOwner] DEFAULT N''dashboard'', [ConfigurationSourceKey] NVARCHAR(160) NULL, [ConfigurationFingerprint] NVARCHAR(64) NULL, [LastReconciledAt] DATETIME2 NULL, [ConfigurationOrphanedAt] DATETIME2 NULL;');
END
IF OBJECT_ID(N'[{Schema}].[SqlOSSsoConnections]', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{Schema}].[SqlOSSsoConnections]') AND [name] = N'UX_SqlOSSsoConnections_ConfigurationOwner_SourceKey')
    EXEC(N'CREATE UNIQUE INDEX [UX_SqlOSSsoConnections_ConfigurationOwner_SourceKey] ON [{Schema}].[SqlOSSsoConnections]([ConfigurationOwner], [ConfigurationSourceKey]) WHERE [ConfigurationSourceKey] IS NOT NULL;');
