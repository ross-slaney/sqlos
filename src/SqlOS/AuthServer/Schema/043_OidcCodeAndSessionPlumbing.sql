IF OBJECT_ID(N'[{Schema}].[SqlOSAuthorizationCodes]', N'U') IS NOT NULL AND COL_LENGTH('[{Schema}].[SqlOSAuthorizationCodes]', 'Nonce') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationCodes]
    ADD [Nonce] NVARCHAR(256) NULL;
END

GO

IF OBJECT_ID(N'[{Schema}].[SqlOSAuthorizationCodes]', N'U') IS NOT NULL AND COL_LENGTH('[{Schema}].[SqlOSAuthorizationCodes]', 'AuthTime') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationCodes]
    ADD [AuthTime] DATETIME2 NULL;
END

GO

IF OBJECT_ID(N'[{Schema}].[SqlOSSessions]', N'U') IS NOT NULL AND COL_LENGTH('[{Schema}].[SqlOSSessions]', 'Scope') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSSessions]
    ADD [Scope] NVARCHAR(1000) NULL;
END

GO

IF OBJECT_ID(N'[{Schema}].[SqlOSSessions]', N'U') IS NOT NULL AND COL_LENGTH('[{Schema}].[SqlOSSessions]', 'AuthenticatedAt') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSSessions]
    ADD [AuthenticatedAt] DATETIME2 NULL;
END

GO

IF OBJECT_ID(N'[{Schema}].[SqlOSDeviceAuthorizations]', N'U') IS NOT NULL AND COL_LENGTH('[{Schema}].[SqlOSDeviceAuthorizations]', 'AuthTime') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSDeviceAuthorizations]
    ADD [AuthTime] DATETIME2 NULL;
END

GO

IF OBJECT_ID(N'[{Schema}].[SqlOSAuthorizationRequests]', N'U') IS NOT NULL AND COL_LENGTH('[{Schema}].[SqlOSAuthorizationRequests]', 'MaxAgeSeconds') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthorizationRequests]
    ADD [MaxAgeSeconds] BIGINT NULL;
END
