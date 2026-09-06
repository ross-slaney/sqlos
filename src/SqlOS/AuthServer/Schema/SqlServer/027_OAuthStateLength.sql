IF OBJECT_ID('{Schema}.SqlOSAuthorizationRequests', 'U') IS NOT NULL
   AND COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'State') > 0
   AND COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'State') < 4096
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ALTER COLUMN [State] NVARCHAR(2048) NOT NULL;
END

GO

IF OBJECT_ID('{Schema}.SqlOSAuthorizationCodes', 'U') IS NOT NULL
   AND COL_LENGTH('{Schema}.SqlOSAuthorizationCodes', 'State') > 0
   AND COL_LENGTH('{Schema}.SqlOSAuthorizationCodes', 'State') < 4096
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationCodes]
    ALTER COLUMN [State] NVARCHAR(2048) NOT NULL;
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (27);
