-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
-- SqlOSFga Schema v2: Users and Agents extension tables
-- All statements are idempotent (NOT EXISTS) for safe execution on existing databases.

-- 1. Users
CREATE TABLE IF NOT EXISTS "{Schema}"."{Users}" (
        "Id"          varchar(450)  NOT NULL,
        "SubjectId"   varchar(450)  NOT NULL,
        "Email"       varchar(450)  NULL,
        "LastLoginAt" timestamp      NULL,
        "IsActive"    boolean            NOT NULL DEFAULT TRUE,
        "CreatedAt"   timestamp      NOT NULL,
        "UpdatedAt"   timestamp      NOT NULL,
        CONSTRAINT "PK_{Users}" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_{Users}_{Subjects}_SubjectId" FOREIGN KEY ("SubjectId")
            REFERENCES "{Schema}"."{Subjects}" ("Id") ON DELETE NO ACTION,
        CONSTRAINT "UQ_{Users}_SubjectId" UNIQUE ("SubjectId")
    );

-- 2. Agents
CREATE TABLE IF NOT EXISTS "{Schema}"."{Agents}" (
        "Id"          varchar(450)  NOT NULL,
        "SubjectId"   varchar(450)  NOT NULL,
        "AgentType"   varchar(450)  NULL,
        "Description" text  NULL,
        "LastRunAt"   timestamp      NULL,
        "CreatedAt"   timestamp      NOT NULL,
        "UpdatedAt"   timestamp      NOT NULL,
        CONSTRAINT "PK_{Agents}" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_{Agents}_{Subjects}_SubjectId" FOREIGN KEY ("SubjectId")
            REFERENCES "{Schema}"."{Subjects}" ("Id") ON DELETE NO ACTION,
        CONSTRAINT "UQ_{Agents}_SubjectId" UNIQUE ("SubjectId")
    );

-- Update schema version to 2
UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 2 WHERE "Version" < 2;
