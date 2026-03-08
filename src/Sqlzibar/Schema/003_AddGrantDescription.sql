-- Sqlzibar Schema v3: Add Description column to Grants table
-- Example migration demonstrating the pattern for future schema changes.

-- Add Description column to Grants table (if it doesn't exist)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('{Schema}.{Grants}') AND name = 'Description')
BEGIN
    ALTER TABLE [{Schema}].[{Grants}] ADD [Description] NVARCHAR(MAX) NULL;
END
GO

-- Update schema version to 3
UPDATE [{Schema}].[SqlzibarSchema] SET [Version] = 3 WHERE [Version] < 3;
GO
