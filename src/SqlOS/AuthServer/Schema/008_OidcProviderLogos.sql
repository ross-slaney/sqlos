IF COL_LENGTH('{Schema}.SqlOSAuthOidcConnections', 'LogoDataUrl') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthOidcConnections]
    ADD [LogoDataUrl] NVARCHAR(MAX) NULL;
END
GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (8);
