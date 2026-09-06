-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
-- SqlOSFga Schema v1: Initial table creation
-- All statements are idempotent (NOT EXISTS) for safe execution on existing databases.

-- 1. SubjectTypes
CREATE TABLE IF NOT EXISTS "{Schema}"."{SubjectTypes}" (
        "Id"          varchar(450)  NOT NULL,
        "Name"        text  NOT NULL,
        "Description" text  NULL,
        CONSTRAINT "PK_{SubjectTypes}" PRIMARY KEY ("Id")
    );

-- 2. Subjects
CREATE TABLE IF NOT EXISTS "{Schema}"."{Subjects}" (
        "Id"             varchar(450)  NOT NULL,
        "SubjectTypeId"  varchar(450)  NOT NULL,
        "OrganizationId" varchar(450)  NULL,
        "ExternalRef"    varchar(450)  NULL,
        "DisplayName"    text  NOT NULL,
        "CreatedAt"      timestamp      NOT NULL,
        "UpdatedAt"      timestamp      NOT NULL,
        CONSTRAINT "PK_{Subjects}" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_{Subjects}_{SubjectTypes}_SubjectTypeId" FOREIGN KEY ("SubjectTypeId")
            REFERENCES "{Schema}"."{SubjectTypes}" ("Id") ON DELETE NO ACTION
    );

-- 3. ResourceTypes
CREATE TABLE IF NOT EXISTS "{Schema}"."{ResourceTypes}" (
        "Id"          varchar(450)  NOT NULL,
        "Name"        text  NOT NULL,
        "Description" text  NULL,
        CONSTRAINT "PK_{ResourceTypes}" PRIMARY KEY ("Id")
    );

-- 4. Resources
CREATE TABLE IF NOT EXISTS "{Schema}"."{Resources}" (
        "Id"             varchar(450)  NOT NULL,
        "ParentId"       varchar(450)  NULL,
        "Name"           text  NOT NULL,
        "Description"    text  NULL,
        "ResourceTypeId" varchar(450)  NOT NULL,
        "IsActive"       boolean            NOT NULL DEFAULT TRUE,
        "CreatedAt"      timestamp      NOT NULL,
        "UpdatedAt"      timestamp      NOT NULL,
        CONSTRAINT "PK_{Resources}" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_{Resources}_{Resources}_ParentId" FOREIGN KEY ("ParentId")
            REFERENCES "{Schema}"."{Resources}" ("Id") ON DELETE NO ACTION,
        CONSTRAINT "FK_{Resources}_{ResourceTypes}_ResourceTypeId" FOREIGN KEY ("ResourceTypeId")
            REFERENCES "{Schema}"."{ResourceTypes}" ("Id") ON DELETE NO ACTION
    );

-- 5. Roles
CREATE TABLE IF NOT EXISTS "{Schema}"."{Roles}" (
        "Id"          varchar(450)  NOT NULL,
        "Key"         text  NOT NULL,
        "Name"        text  NOT NULL,
        "Description" text  NULL,
        "IsVirtual"   boolean            NOT NULL DEFAULT FALSE,
        CONSTRAINT "PK_{Roles}" PRIMARY KEY ("Id")
    );

-- 6. Permissions
CREATE TABLE IF NOT EXISTS "{Schema}"."{Permissions}" (
        "Id"             varchar(450)  NOT NULL,
        "ResourceTypeId" varchar(450)  NULL,
        "Key"            text  NOT NULL,
        "Name"           text  NOT NULL,
        "Description"    text  NULL,
        CONSTRAINT "PK_{Permissions}" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_{Permissions}_{ResourceTypes}_ResourceTypeId" FOREIGN KEY ("ResourceTypeId")
            REFERENCES "{Schema}"."{ResourceTypes}" ("Id") ON DELETE NO ACTION
    );

-- 7. RolePermissions
CREATE TABLE IF NOT EXISTS "{Schema}"."{RolePermissions}" (
        "RoleId"       varchar(450)  NOT NULL,
        "PermissionId" varchar(450)  NOT NULL,
        CONSTRAINT "PK_{RolePermissions}" PRIMARY KEY ("RoleId", "PermissionId"),
        CONSTRAINT "FK_{RolePermissions}_{Roles}_RoleId" FOREIGN KEY ("RoleId")
            REFERENCES "{Schema}"."{Roles}" ("Id") ON DELETE NO ACTION,
        CONSTRAINT "FK_{RolePermissions}_{Permissions}_PermissionId" FOREIGN KEY ("PermissionId")
            REFERENCES "{Schema}"."{Permissions}" ("Id") ON DELETE NO ACTION
    );

-- 8. Grants
CREATE TABLE IF NOT EXISTS "{Schema}"."{Grants}" (
        "Id"            varchar(450)  NOT NULL,
        "SubjectId"     varchar(450)  NOT NULL,
        "ResourceId"    varchar(450)  NOT NULL,
        "RoleId"        varchar(450)  NOT NULL,
        "EffectiveFrom" timestamp      NULL,
        "EffectiveTo"   timestamp      NULL,
        "CreatedAt"     timestamp      NOT NULL,
        "UpdatedAt"     timestamp      NOT NULL,
        CONSTRAINT "PK_{Grants}" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_{Grants}_{Subjects}_SubjectId" FOREIGN KEY ("SubjectId")
            REFERENCES "{Schema}"."{Subjects}" ("Id") ON DELETE NO ACTION,
        CONSTRAINT "FK_{Grants}_{Resources}_ResourceId" FOREIGN KEY ("ResourceId")
            REFERENCES "{Schema}"."{Resources}" ("Id") ON DELETE NO ACTION,
        CONSTRAINT "FK_{Grants}_{Roles}_RoleId" FOREIGN KEY ("RoleId")
            REFERENCES "{Schema}"."{Roles}" ("Id") ON DELETE NO ACTION
    );

-- 9. UserGroups
CREATE TABLE IF NOT EXISTS "{Schema}"."{UserGroups}" (
        "Id"          varchar(450)  NOT NULL,
        "Name"        text  NOT NULL,
        "Description" text  NULL,
        "GroupType"   text  NULL,
        "SubjectId"   varchar(450)  NOT NULL,
        "IsActive"    boolean            NOT NULL DEFAULT TRUE,
        "CreatedAt"   timestamp      NOT NULL,
        "UpdatedAt"   timestamp      NOT NULL,
        CONSTRAINT "PK_{UserGroups}" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_{UserGroups}_{Subjects}_SubjectId" FOREIGN KEY ("SubjectId")
            REFERENCES "{Schema}"."{Subjects}" ("Id") ON DELETE NO ACTION,
        CONSTRAINT "UQ_{UserGroups}_SubjectId" UNIQUE ("SubjectId")
    );

-- 10. UserGroupMemberships
CREATE TABLE IF NOT EXISTS "{Schema}"."{UserGroupMemberships}" (
        "SubjectId"   varchar(450)  NOT NULL,
        "UserGroupId" varchar(450)  NOT NULL,
        "CreatedAt"   timestamp      NOT NULL,
        CONSTRAINT "PK_{UserGroupMemberships}" PRIMARY KEY ("SubjectId", "UserGroupId"),
        CONSTRAINT "FK_{UserGroupMemberships}_{Subjects}_SubjectId" FOREIGN KEY ("SubjectId")
            REFERENCES "{Schema}"."{Subjects}" ("Id") ON DELETE NO ACTION,
        CONSTRAINT "FK_{UserGroupMemberships}_{UserGroups}_UserGroupId" FOREIGN KEY ("UserGroupId")
            REFERENCES "{Schema}"."{UserGroups}" ("Id") ON DELETE NO ACTION
    );

-- 11. ServiceAccounts
CREATE TABLE IF NOT EXISTS "{Schema}"."{ServiceAccounts}" (
        "Id"               varchar(450)  NOT NULL,
        "SubjectId"        varchar(450)  NOT NULL,
        "ClientId"         text  NOT NULL,
        "ClientSecretHash" text  NOT NULL,
        "Description"      text  NULL,
        "LastUsedAt"       timestamp      NULL,
        "ExpiresAt"        timestamp      NULL,
        "CreatedAt"        timestamp      NOT NULL,
        "UpdatedAt"        timestamp      NOT NULL,
        CONSTRAINT "PK_{ServiceAccounts}" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_{ServiceAccounts}_{Subjects}_SubjectId" FOREIGN KEY ("SubjectId")
            REFERENCES "{Schema}"."{Subjects}" ("Id") ON DELETE NO ACTION,
        CONSTRAINT "UQ_{ServiceAccounts}_SubjectId" UNIQUE ("SubjectId")
    );

-- 12. SqlOSFgaSchema (version tracking)
CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSFgaSchema" (
        "Version" INT NOT NULL
    );

-- Set schema version to 1
INSERT INTO "{Schema}"."SqlOSFgaSchema" ("Version")
SELECT 1
WHERE NOT EXISTS (SELECT 1 FROM "{Schema}"."SqlOSFgaSchema");
UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 1 WHERE "Version" < 1;
