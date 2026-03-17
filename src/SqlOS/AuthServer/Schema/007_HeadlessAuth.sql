IF COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'PresentationMode') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ADD [PresentationMode] NVARCHAR(32) NOT NULL
        CONSTRAINT [DF_SqlOSAuthorizationRequests_PresentationMode] DEFAULT 'hosted';
END
GO

IF COL_LENGTH('{Schema}.SqlOSAuthorizationRequests', 'UiContextJson') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ADD [UiContextJson] NVARCHAR(MAX) NULL;
END
GO
