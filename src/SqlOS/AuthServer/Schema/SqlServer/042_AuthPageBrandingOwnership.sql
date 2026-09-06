IF OBJECT_ID(N'[{Schema}].[SqlOSAuthPageSettings]', N'U') IS NOT NULL AND COL_LENGTH('[{Schema}].[SqlOSAuthPageSettings]', 'AuthPageConfigurationOwner') IS NULL
BEGIN
    EXEC(N'ALTER TABLE [{Schema}].[SqlOSAuthPageSettings] ADD
        [AuthPageConfigurationOwner] NVARCHAR(40) NOT NULL CONSTRAINT [DF_SqlOSAuthPageSettings_AuthPageConfigurationOwner] DEFAULT N''system'',
        [AuthPageConfigurationSourceKey] NVARCHAR(160) NULL,
        [AuthPageConfigurationFingerprint] NVARCHAR(64) NULL,
        [AuthPageLastReconciledAt] DATETIME2 NULL,
        [AuthPageConfigurationOrphanedAt] DATETIME2 NULL,
        [EmailConfigurationOwner] NVARCHAR(40) NOT NULL CONSTRAINT [DF_SqlOSAuthPageSettings_EmailConfigurationOwner] DEFAULT N''system'',
        [EmailConfigurationSourceKey] NVARCHAR(160) NULL,
        [EmailConfigurationFingerprint] NVARCHAR(64) NULL,
        [EmailLastReconciledAt] DATETIME2 NULL,
        [EmailConfigurationOrphanedAt] DATETIME2 NULL;');
END
