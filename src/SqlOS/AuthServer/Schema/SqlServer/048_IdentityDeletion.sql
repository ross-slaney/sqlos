IF OBJECT_ID('[{Schema}].[SqlOSUsers]', 'U') IS NOT NULL AND COL_LENGTH('[{Schema}].[SqlOSUsers]', 'DeletedAt') IS NULL
    ALTER TABLE [{Schema}].[SqlOSUsers] ADD [DeletedAt] DATETIME2 NULL;
IF OBJECT_ID('[{Schema}].[SqlOSOrganizations]', 'U') IS NOT NULL AND COL_LENGTH('[{Schema}].[SqlOSOrganizations]', 'DeletedAt') IS NULL
    ALTER TABLE [{Schema}].[SqlOSOrganizations] ADD [DeletedAt] DATETIME2 NULL;
