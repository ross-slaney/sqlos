-- SqlOSFga Schema v2: Users and Agents extension tables
-- All statements are idempotent (IF NOT EXISTS) for safe execution on existing databases.

-- 1. Users
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = '{Users}' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[{Users}] (
        [Id]          NVARCHAR(450)  NOT NULL,
        [SubjectId]   NVARCHAR(450)  NOT NULL,
        [Email]       NVARCHAR(450)  NULL,
        [LastLoginAt] DATETIME2      NULL,
        [IsActive]    BIT            NOT NULL DEFAULT 1,
        [CreatedAt]   DATETIME2      NOT NULL,
        [UpdatedAt]   DATETIME2      NOT NULL,
        CONSTRAINT [PK_{Users}] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_{Users}_{Subjects}_SubjectId] FOREIGN KEY ([SubjectId])
            REFERENCES [{Schema}].[{Subjects}] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [UQ_{Users}_SubjectId] UNIQUE ([SubjectId])
    );
END
GO

-- 2. Agents
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = '{Agents}' AND schema_id = SCHEMA_ID('{Schema}'))
BEGIN
    CREATE TABLE [{Schema}].[{Agents}] (
        [Id]          NVARCHAR(450)  NOT NULL,
        [SubjectId]   NVARCHAR(450)  NOT NULL,
        [AgentType]   NVARCHAR(450)  NULL,
        [Description] NVARCHAR(MAX)  NULL,
        [LastRunAt]   DATETIME2      NULL,
        [CreatedAt]   DATETIME2      NOT NULL,
        [UpdatedAt]   DATETIME2      NOT NULL,
        CONSTRAINT [PK_{Agents}] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_{Agents}_{Subjects}_SubjectId] FOREIGN KEY ([SubjectId])
            REFERENCES [{Schema}].[{Subjects}] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [UQ_{Agents}_SubjectId] UNIQUE ([SubjectId])
    );
END
GO

-- Update schema version to 2
UPDATE [{Schema}].[SqlOSFgaSchema] SET [Version] = 2 WHERE [Version] < 2;
