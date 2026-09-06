IF OBJECT_ID(N'[{Schema}].[SqlOSSsoConnections]', N'U') IS NOT NULL
    AND COL_LENGTH('[{Schema}].[SqlOSSsoConnections]', 'TrustUpstreamMfa') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSSsoConnections]
        ADD [TrustUpstreamMfa] BIT NOT NULL
            CONSTRAINT [DF_SqlOSSsoConnections_TrustUpstreamMfa] DEFAULT 0;
END

IF OBJECT_ID(N'[{Schema}].[SqlOSSsoConnections]', N'U') IS NOT NULL
    AND COL_LENGTH('[{Schema}].[SqlOSSsoConnections]', 'AcceptedAuthnContextClassRefsJson') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSSsoConnections]
        ADD [AcceptedAuthnContextClassRefsJson] NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_SqlOSSsoConnections_AcceptedAuthnContextClassRefsJson] DEFAULT N'[]';
END

IF OBJECT_ID(N'[{Schema}].[SqlOSAuthOidcConnections]', N'U') IS NOT NULL
    AND COL_LENGTH('[{Schema}].[SqlOSAuthOidcConnections]', 'TrustUpstreamMfa') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthOidcConnections]
        ADD [TrustUpstreamMfa] BIT NOT NULL
            CONSTRAINT [DF_SqlOSAuthOidcConnections_TrustUpstreamMfa] DEFAULT 0;
END

IF OBJECT_ID(N'[{Schema}].[SqlOSAuthOidcConnections]', N'U') IS NOT NULL
    AND COL_LENGTH('[{Schema}].[SqlOSAuthOidcConnections]', 'AcceptedAmrValuesJson') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthOidcConnections]
        ADD [AcceptedAmrValuesJson] NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_SqlOSAuthOidcConnections_AcceptedAmrValuesJson] DEFAULT N'[]';
END

IF OBJECT_ID(N'[{Schema}].[SqlOSAuthOidcConnections]', N'U') IS NOT NULL
    AND COL_LENGTH('[{Schema}].[SqlOSAuthOidcConnections]', 'AcceptedAcrValuesJson') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSAuthOidcConnections]
        ADD [AcceptedAcrValuesJson] NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_SqlOSAuthOidcConnections_AcceptedAcrValuesJson] DEFAULT N'[]';
END
