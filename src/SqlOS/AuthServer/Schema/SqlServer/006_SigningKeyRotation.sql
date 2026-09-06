IF COL_LENGTH('{Schema}.SqlOSSettings', 'SigningKeyRotationIntervalDays') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSSettings]
    ADD [SigningKeyRotationIntervalDays] INT NOT NULL CONSTRAINT [DF_SqlOSSettings_RotationInterval] DEFAULT 90;
END

GO

IF COL_LENGTH('{Schema}.SqlOSSettings', 'SigningKeyGraceWindowDays') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSSettings]
    ADD [SigningKeyGraceWindowDays] INT NOT NULL CONSTRAINT [DF_SqlOSSettings_GraceWindow] DEFAULT 7;
END

GO

IF COL_LENGTH('{Schema}.SqlOSSettings', 'SigningKeyRetiredCleanupDays') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[SqlOSSettings]
    ADD [SigningKeyRetiredCleanupDays] INT NOT NULL CONSTRAINT [DF_SqlOSSettings_RetiredCleanup] DEFAULT 30;
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (6);
