IF COL_LENGTH('{Schema}.SqlOSSettings', 'RefreshTokenGraceWindowSeconds') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSSettings]
    ADD [RefreshTokenGraceWindowSeconds] INT NOT NULL CONSTRAINT [DF_SqlOSSettings_RefreshTokenGraceWindowSeconds] DEFAULT 30;
END

GO

IF COL_LENGTH('{Schema}.SqlOSRefreshTokens', 'ReplacementAccessToken') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSRefreshTokens]
    ADD [ReplacementAccessToken] NVARCHAR(MAX) NULL;
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (10);
