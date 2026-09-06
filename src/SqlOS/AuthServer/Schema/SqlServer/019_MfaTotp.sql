IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSMfaSettings' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSMfaSettings] (
        [Id] NVARCHAR(64) NOT NULL CONSTRAINT [PK_SqlOSMfaSettings] PRIMARY KEY,
        [Enabled] BIT NOT NULL,
        [TotpEnabled] BIT NOT NULL,
        [UserSelfEnrollmentEnabled] BIT NOT NULL,
        [RecoveryCodesEnabled] BIT NOT NULL,
        [RequireForAllUsers] BIT NOT NULL,
        [RequireForOwnersAndAdmins] BIT NOT NULL,
        [RequiredRolesJson] NVARCHAR(MAX) NOT NULL,
        [AvailableFactorsJson] NVARCHAR(MAX) NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL
    );
END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSOrganizationMfaPolicies' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSOrganizationMfaPolicies] (
        [OrganizationId] NVARCHAR(64) NOT NULL CONSTRAINT [PK_SqlOSOrganizationMfaPolicies] PRIMARY KEY,
        [IsEnabled] BIT NOT NULL,
        [RequireMfaForAllUsers] BIT NOT NULL,
        [RequireMfaForOwnersAndAdmins] BIT NOT NULL,
        [UserSelfEnrollmentEnabled] BIT NOT NULL,
        [RecoveryCodesEnabled] BIT NOT NULL,
        [RequiredRolesJson] NVARCHAR(MAX) NOT NULL,
        [AvailableFactorsJson] NVARCHAR(MAX) NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL
    );

    ALTER TABLE [{Schema}].[SqlOSOrganizationMfaPolicies]
        ADD CONSTRAINT [FK_SqlOSOrganizationMfaPolicies_Organizations_OrganizationId]
            FOREIGN KEY ([OrganizationId]) REFERENCES [{Schema}].[SqlOSOrganizations]([Id]);
END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSUserMfaPolicyOverrides' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSUserMfaPolicyOverrides] (
        [UserId] NVARCHAR(64) NOT NULL CONSTRAINT [PK_SqlOSUserMfaPolicyOverrides] PRIMARY KEY,
        [RequireMfa] BIT NULL,
        [UserSelfEnrollmentEnabled] BIT NULL,
        [UpdatedAt] DATETIME2 NOT NULL
    );

    ALTER TABLE [{Schema}].[SqlOSUserMfaPolicyOverrides]
        ADD CONSTRAINT [FK_SqlOSUserMfaPolicyOverrides_Users_UserId]
            FOREIGN KEY ([UserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id]);
END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSUserAuthenticators' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSUserAuthenticators] (
        [Id] NVARCHAR(64) NOT NULL CONSTRAINT [PK_SqlOSUserAuthenticators] PRIMARY KEY,
        [UserId] NVARCHAR(64) NOT NULL,
        [Type] NVARCHAR(40) NOT NULL,
        [DisplayName] NVARCHAR(120) NOT NULL,
        [SecretProtected] NVARCHAR(2048) NOT NULL,
        [SecretVersion] INT NOT NULL,
        [Algorithm] NVARCHAR(20) NOT NULL,
        [Digits] INT NOT NULL,
        [PeriodSeconds] INT NOT NULL,
        [IsConfirmed] BIT NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [ConfirmedAt] DATETIME2 NULL,
        [LastUsedAt] DATETIME2 NULL,
        [RevokedAt] DATETIME2 NULL,
        [RevocationReason] NVARCHAR(120) NULL,
        [LastAcceptedTimeStep] BIGINT NULL
    );

    CREATE INDEX [IX_SqlOSUserAuthenticators_User_Type_Revoked]
        ON [{Schema}].[SqlOSUserAuthenticators]([UserId], [Type], [RevokedAt]);

    CREATE INDEX [IX_SqlOSUserAuthenticators_User_Confirmed_Revoked]
        ON [{Schema}].[SqlOSUserAuthenticators]([UserId], [IsConfirmed], [RevokedAt]);

    ALTER TABLE [{Schema}].[SqlOSUserAuthenticators]
        ADD CONSTRAINT [FK_SqlOSUserAuthenticators_Users_UserId]
            FOREIGN KEY ([UserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id]);
END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSRecoveryCodes' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSRecoveryCodes] (
        [Id] NVARCHAR(64) NOT NULL CONSTRAINT [PK_SqlOSRecoveryCodes] PRIMARY KEY,
        [UserId] NVARCHAR(64) NOT NULL,
        [CodeHash] NVARCHAR(128) NOT NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [ConsumedAt] DATETIME2 NULL,
        [RevokedAt] DATETIME2 NULL
    );

    CREATE UNIQUE INDEX [IX_SqlOSRecoveryCodes_CodeHash]
        ON [{Schema}].[SqlOSRecoveryCodes]([CodeHash]);

    CREATE INDEX [IX_SqlOSRecoveryCodes_User_Consumed_Revoked]
        ON [{Schema}].[SqlOSRecoveryCodes]([UserId], [ConsumedAt], [RevokedAt]);

    ALTER TABLE [{Schema}].[SqlOSRecoveryCodes]
        ADD CONSTRAINT [FK_SqlOSRecoveryCodes_Users_UserId]
            FOREIGN KEY ([UserId]) REFERENCES [{Schema}].[SqlOSUsers]([Id]);
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (19);
