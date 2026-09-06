IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSEmailTemplates' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSEmailTemplates] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [Key] NVARCHAR(120) NOT NULL,
        [DisplayName] NVARCHAR(200) NOT NULL,
        [SubjectTemplate] NVARCHAR(500) NOT NULL,
        [HtmlBodyTemplate] NVARCHAR(MAX) NOT NULL,
        [TextBodyTemplate] NVARCHAR(MAX) NOT NULL,
        [VariablesJson] NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_SqlOSEmailTemplates_VariablesJson] DEFAULT '{{}}',
        [IsActive] BIT NOT NULL
            CONSTRAINT [DF_SqlOSEmailTemplates_IsActive] DEFAULT 1,
        [Version] INT NOT NULL
            CONSTRAINT [DF_SqlOSEmailTemplates_Version] DEFAULT 1,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL
    );
END

GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSEmailTemplates_Key' AND object_id = OBJECT_ID('{Schema}.SqlOSEmailTemplates'))
BEGIN
    CREATE UNIQUE INDEX [IX_SqlOSEmailTemplates_Key]
    ON [{Schema}].[SqlOSEmailTemplates]([Key]);
END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSEmailDeliveries' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[SqlOSEmailDeliveries] (
        [Id] NVARCHAR(64) NOT NULL PRIMARY KEY,
        [TemplateId] NVARCHAR(64) NULL,
        [TemplateKey] NVARCHAR(120) NOT NULL,
        [TemplateVersion] INT NOT NULL,
        [To] NVARCHAR(320) NOT NULL,
        [Status] NVARCHAR(32) NOT NULL,
        [ProviderMessageId] NVARCHAR(200) NULL,
        [SanitizedError] NVARCHAR(500) NULL,
        [RenderedSubject] NVARCHAR(500) NOT NULL,
        [RenderedTextPreview] NVARCHAR(MAX) NOT NULL,
        [RenderedHtmlPreview] NVARCHAR(MAX) NULL,
        [IdempotencyKey] NVARCHAR(200) NULL,
        [CreatedAt] DATETIME2 NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL,
        [SentAt] DATETIME2 NULL,
        [FailedAt] DATETIME2 NULL,
        CONSTRAINT [FK_SqlOSEmailDeliveries_SqlOSEmailTemplates_TemplateId]
            FOREIGN KEY ([TemplateId])
            REFERENCES [{Schema}].[SqlOSEmailTemplates]([Id])
            ON DELETE SET NULL
    );
END

GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSEmailDeliveries_TemplateKeyCreatedAt' AND object_id = OBJECT_ID('{Schema}.SqlOSEmailDeliveries'))
BEGIN
    CREATE INDEX [IX_SqlOSEmailDeliveries_TemplateKeyCreatedAt]
    ON [{Schema}].[SqlOSEmailDeliveries]([TemplateKey], [CreatedAt]);
END

GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSEmailDeliveries_StatusCreatedAt' AND object_id = OBJECT_ID('{Schema}.SqlOSEmailDeliveries'))
BEGIN
    CREATE INDEX [IX_SqlOSEmailDeliveries_StatusCreatedAt]
    ON [{Schema}].[SqlOSEmailDeliveries]([Status], [CreatedAt]);
END

GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSEmailDeliveries_ToCreatedAt' AND object_id = OBJECT_ID('{Schema}.SqlOSEmailDeliveries'))
BEGIN
    CREATE INDEX [IX_SqlOSEmailDeliveries_ToCreatedAt]
    ON [{Schema}].[SqlOSEmailDeliveries]([To], [CreatedAt]);
END

GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSEmailDeliveries_CreatedAt' AND object_id = OBJECT_ID('{Schema}.SqlOSEmailDeliveries'))
BEGIN
    CREATE INDEX [IX_SqlOSEmailDeliveries_CreatedAt]
    ON [{Schema}].[SqlOSEmailDeliveries]([CreatedAt]);
END

GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlOSEmailDeliveries_IdempotencyKey' AND object_id = OBJECT_ID('{Schema}.SqlOSEmailDeliveries'))
BEGIN
    CREATE UNIQUE INDEX [IX_SqlOSEmailDeliveries_IdempotencyKey]
    ON [{Schema}].[SqlOSEmailDeliveries]([IdempotencyKey])
    WHERE [IdempotencyKey] IS NOT NULL;
END

GO

DELETE FROM [{Schema}].[SqlOSSchema];
INSERT INTO [{Schema}].[SqlOSSchema] ([Version]) VALUES (17);
