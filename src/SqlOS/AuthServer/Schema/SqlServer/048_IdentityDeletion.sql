IF COL_LENGTH('[{Schema}].[SqlOSUsers]', 'DeletedAt') IS NULL
    ALTER TABLE [{Schema}].[SqlOSUsers] ADD [DeletedAt] DATETIME2 NULL;
IF COL_LENGTH('[{Schema}].[SqlOSOrganizations]', 'DeletedAt') IS NULL
    ALTER TABLE [{Schema}].[SqlOSOrganizations] ADD [DeletedAt] DATETIME2 NULL;
