IF COL_LENGTH('{Schema}.SqlOSClientApplications', 'AllowNativeHeadlessAuth') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSClientApplications]
    ADD [AllowNativeHeadlessAuth] BIT NOT NULL CONSTRAINT [DF_SqlOSClientApplications_AllowNativeHeadlessAuth] DEFAULT 0;
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (11);
