IF COL_LENGTH('{Schema}.SqlOSAuthPageSettings', 'EmailApplicationName') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthPageSettings]
    ADD [EmailApplicationName] NVARCHAR(200) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthPageSettings', 'EmailLogoBase64') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthPageSettings]
    ADD [EmailLogoBase64] NVARCHAR(MAX) NULL;
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthPageSettings', 'EmailPrimaryColor') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthPageSettings]
    ADD [EmailPrimaryColor] NVARCHAR(32) NOT NULL
        CONSTRAINT [DF_SqlOSAuthPageSettings_EmailPrimaryColor] DEFAULT N'#2563eb';
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthPageSettings', 'EmailAccentColor') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthPageSettings]
    ADD [EmailAccentColor] NVARCHAR(32) NOT NULL
        CONSTRAINT [DF_SqlOSAuthPageSettings_EmailAccentColor] DEFAULT N'#0f172a';
END

GO

IF COL_LENGTH('{Schema}.SqlOSAuthPageSettings', 'EmailBackgroundColor') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthPageSettings]
    ADD [EmailBackgroundColor] NVARCHAR(32) NOT NULL
        CONSTRAINT [DF_SqlOSAuthPageSettings_EmailBackgroundColor] DEFAULT N'#f8fafc';
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (14);
