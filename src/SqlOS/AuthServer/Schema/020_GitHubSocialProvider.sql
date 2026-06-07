IF COL_LENGTH('{Schema}.SqlOSAuthOidcConnections', 'Protocol') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthOidcConnections]
    ADD [Protocol] NVARCHAR(40) NOT NULL
        CONSTRAINT [DF_SqlOSAuthOidcConnections_Protocol] DEFAULT ('Oidc');
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (20);
