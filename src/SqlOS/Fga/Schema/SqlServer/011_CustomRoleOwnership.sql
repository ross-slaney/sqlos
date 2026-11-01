IF COL_LENGTH('{Schema}.{Roles}', 'DefinitionSource') IS NULL
BEGIN
    ALTER TABLE [{Schema}].[{Roles}] ADD
        [DefinitionSource] NVARCHAR(16) NOT NULL CONSTRAINT [DF_{Roles}_DefinitionSource] DEFAULT 'application',
        [OwnerResourceId] NVARCHAR(450) NULL,
        [Revision] BIGINT NOT NULL CONSTRAINT [DF_{Roles}_Revision] DEFAULT 1,
        [ArchivedAt] DATETIME2 NULL;
END
GO
IF COL_LENGTH('{Schema}.{Permissions}', 'AllowInCustomRoles') IS NULL
    ALTER TABLE [{Schema}].[{Permissions}] ADD [AllowInCustomRoles] BIT NOT NULL CONSTRAINT [DF_{Permissions}_AllowInCustomRoles] DEFAULT 0;
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_{Roles}_OwnerResource' AND parent_object_id = OBJECT_ID('[{Schema}].[{Roles}]'))
    ALTER TABLE [{Schema}].[{Roles}] ADD CONSTRAINT [FK_{Roles}_OwnerResource] FOREIGN KEY ([OwnerResourceId]) REFERENCES [{Schema}].[{Resources}]([Id]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_{Roles}_Ownership' AND parent_object_id = OBJECT_ID('[{Schema}].[{Roles}]'))
    ALTER TABLE [{Schema}].[{Roles}] ADD CONSTRAINT [CK_{Roles}_Ownership] CHECK (
        ([DefinitionSource] = 'application' AND [OwnerResourceId] IS NULL AND [ArchivedAt] IS NULL) OR
        ([DefinitionSource] = 'customer' AND [OwnerResourceId] IS NOT NULL AND [IsVirtual] = 0));
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_{Roles}_OwnerResourceId' AND object_id = OBJECT_ID('[{Schema}].[{Roles}]'))
    CREATE INDEX [IX_{Roles}_OwnerResourceId] ON [{Schema}].[{Roles}]([OwnerResourceId]);
GO
UPDATE [{Schema}].[SqlOSFgaSchema] SET [Version] = 11 WHERE [Version] < 11;
GO
