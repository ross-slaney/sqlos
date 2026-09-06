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

IF COL_LENGTH('{Schema}.SqlOSAuthPageSettings', 'PresentationMode') IS NOT NULL
BEGIN
    DECLARE @constraintName sysname;

    SELECT @constraintName = dc.name
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c
        ON c.default_object_id = dc.object_id
    WHERE dc.parent_object_id = OBJECT_ID('[{Schema}].[SqlOSAuthPageSettings]')
      AND c.name = 'PresentationMode';

    IF @constraintName IS NOT NULL
    BEGIN
        EXEC('ALTER TABLE [{Schema}].[SqlOSAuthPageSettings] DROP CONSTRAINT [' + @constraintName + ']');
    END

    ALTER TABLE [{Schema}].[SqlOSAuthPageSettings]
    DROP COLUMN [PresentationMode];
END
GO
