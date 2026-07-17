IF OBJECT_ID(N'[{Schema}].[SqlOSClientApplications]', N'U') IS NOT NULL AND COL_LENGTH('[{Schema}].[SqlOSClientApplications]', 'ConfigurationOwner') IS NULL
BEGIN
    EXEC(N'ALTER TABLE [{Schema}].[SqlOSClientApplications] ADD [ConfigurationOwner] NVARCHAR(40) NOT NULL CONSTRAINT [DF_SqlOSClientApplications_ConfigurationOwner] DEFAULT N''dashboard'', [ConfigurationSourceKey] NVARCHAR(160) NULL, [ConfigurationFingerprint] NVARCHAR(64) NULL, [LastReconciledAt] DATETIME2 NULL, [ConfigurationOrphanedAt] DATETIME2 NULL;');
    IF COL_LENGTH('[{Schema}].[SqlOSClientApplications]', 'RegistrationSource') IS NOT NULL
        AND COL_LENGTH('[{Schema}].[SqlOSClientApplications]', 'ClientId') IS NOT NULL
        EXEC(N'UPDATE [{Schema}].[SqlOSClientApplications] SET [ConfigurationOwner] = CASE WHEN [RegistrationSource] = N''seeded'' THEN N''code'' WHEN [RegistrationSource] IN (N''dcr'', N''cimd'') THEN N''dynamic'' ELSE N''dashboard'' END, [ConfigurationSourceKey] = CASE WHEN [RegistrationSource] = N''seeded'' THEN [ClientId] ELSE NULL END;');
END
IF OBJECT_ID(N'[{Schema}].[SqlOSClientApplications]', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{Schema}].[SqlOSClientApplications]') AND [name] = N'UX_SqlOSClientApplications_ConfigurationOwner_SourceKey')
    EXEC(N'CREATE UNIQUE INDEX [UX_SqlOSClientApplications_ConfigurationOwner_SourceKey] ON [{Schema}].[SqlOSClientApplications]([ConfigurationOwner], [ConfigurationSourceKey]) WHERE [ConfigurationSourceKey] IS NOT NULL;');
IF OBJECT_ID(N'[{Schema}].[SqlOSAuthOidcConnections]', N'U') IS NOT NULL AND COL_LENGTH('[{Schema}].[SqlOSAuthOidcConnections]', 'ConfigurationOwner') IS NULL
BEGIN
    EXEC(N'ALTER TABLE [{Schema}].[SqlOSAuthOidcConnections] ADD [ConfigurationOwner] NVARCHAR(40) NOT NULL CONSTRAINT [DF_SqlOSAuthOidcConnections_ConfigurationOwner] DEFAULT N''dashboard'', [ConfigurationSourceKey] NVARCHAR(160) NULL, [ConfigurationFingerprint] NVARCHAR(64) NULL, [LastReconciledAt] DATETIME2 NULL, [ConfigurationOrphanedAt] DATETIME2 NULL;');
END
IF OBJECT_ID(N'[{Schema}].[SqlOSAuthOidcConnections]', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{Schema}].[SqlOSAuthOidcConnections]') AND [name] = N'UX_SqlOSAuthOidcConnections_ConfigurationOwner_SourceKey')
    EXEC(N'CREATE UNIQUE INDEX [UX_SqlOSAuthOidcConnections_ConfigurationOwner_SourceKey] ON [{Schema}].[SqlOSAuthOidcConnections]([ConfigurationOwner], [ConfigurationSourceKey]) WHERE [ConfigurationSourceKey] IS NOT NULL;');
IF OBJECT_ID(N'[{Schema}].[SqlOSScimConnections]', N'U') IS NOT NULL AND COL_LENGTH('[{Schema}].[SqlOSScimConnections]', 'ConfigurationOwner') IS NULL
BEGIN
    EXEC(N'ALTER TABLE [{Schema}].[SqlOSScimConnections] ADD [ConfigurationOwner] NVARCHAR(40) NOT NULL CONSTRAINT [DF_SqlOSScimConnections_ConfigurationOwner] DEFAULT N''dashboard'', [ConfigurationSourceKey] NVARCHAR(160) NULL, [ConfigurationFingerprint] NVARCHAR(64) NULL, [LastReconciledAt] DATETIME2 NULL, [ConfigurationOrphanedAt] DATETIME2 NULL;');
    EXEC(N'UPDATE [{Schema}].[SqlOSScimConnections] SET [ConfigurationOwner] = CASE WHEN [Source] = N''seeded'' THEN N''code'' ELSE N''dashboard'' END, [ConfigurationSourceKey] = CASE WHEN [Source] = N''seeded'' THEN [SeedKey] ELSE NULL END;');
END
IF OBJECT_ID(N'[{Schema}].[SqlOSScimConnections]', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{Schema}].[SqlOSScimConnections]') AND [name] = N'UX_SqlOSScimConnections_Organization_Owner_SourceKey')
    EXEC(N'CREATE UNIQUE INDEX [UX_SqlOSScimConnections_Organization_Owner_SourceKey] ON [{Schema}].[SqlOSScimConnections]([OrganizationId], [ConfigurationOwner], [ConfigurationSourceKey]) WHERE [ConfigurationSourceKey] IS NOT NULL;');
IF OBJECT_ID(N'[{Schema}].[SqlOSMfaSettings]', N'U') IS NOT NULL AND COL_LENGTH('[{Schema}].[SqlOSMfaSettings]', 'ConfigurationOwner') IS NULL
BEGIN
    EXEC(N'ALTER TABLE [{Schema}].[SqlOSMfaSettings] ADD [ConfigurationOwner] NVARCHAR(40) NOT NULL CONSTRAINT [DF_SqlOSMfaSettings_ConfigurationOwner] DEFAULT N''system'', [ConfigurationSourceKey] NVARCHAR(160) NULL, [ConfigurationFingerprint] NVARCHAR(64) NULL, [LastReconciledAt] DATETIME2 NULL, [ConfigurationOrphanedAt] DATETIME2 NULL;');
END
