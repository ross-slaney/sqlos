# Sqlzibar Schema Changes Guide

This document is for **library maintainers** who need to add, modify, or upgrade the Sqlzibar database schema.

## How Schema Versioning Works

Sqlzibar uses a Hangfire-style raw SQL schema versioning system:

1. A `SqlzibarSchema` table tracks the current schema version (single `Version` int column)
2. Embedded SQL scripts in `src/Sqlzibar/Schema/` define each version
3. On startup, `SqlzibarSchemaInitializer.EnsureSchemaAsync()` runs automatically (via `UseSqlzibarAsync()`)
4. The initializer checks the current version and runs any pending upgrade scripts in order

### Startup Flow

```
UseSqlzibarAsync()
  1. Schema Init  → Check version table → Run pending scripts
  2. Function Init → Create/update fn_IsResourceAccessible TVF
  3. Seed Core     → Seed subject types + root resource
```

### Script Conventions

- Scripts are embedded resources at `src/Sqlzibar/Schema/*.sql`
- Named `NNN_DescriptiveName.sql` (e.g., `001_Initial.sql`, `002_AddEffectiveFromIndex.sql`)
- Use `{Schema}`, `{Subjects}`, `{Resources}`, etc. as placeholders (replaced at runtime from `SqlzibarOptions`)
- Use `GO` to separate batches (the initializer splits on `GO` lines)
- All DDL should be idempotent where possible (`IF NOT EXISTS` for new objects, `IF COL_NAME(...)` checks for columns)

## Adding a New Schema Version

Migration scripts are **auto-discovered** based on the naming convention. Just add the file and it will be picked up automatically.

1. **Create the script** in `src/Sqlzibar/Schema/` with the naming pattern `NNN_Name.sql`:

   ```
   004_AddSomeFeature.sql
   ```

   The number prefix determines the execution order. Scripts are run in ascending order.

   Example content (from actual v3 migration):
   ```sql
   -- Sqlzibar Schema v3: Add Description column to Grants table

   -- Add Description column to Grants table (if it doesn't exist)
   IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('{Schema}.{Grants}') AND name = 'Description')
   BEGIN
       ALTER TABLE [{Schema}].[{Grants}] ADD [Description] NVARCHAR(MAX) NULL;
   END
   GO

   -- Update schema version
   UPDATE [{Schema}].[SqlzibarSchema] SET [Version] = 3 WHERE [Version] < 3;
   GO
   ```

2. **Update the model** if needed. If the script adds a column, update the corresponding model class:

   ```csharp
   // In SqlzibarGrant.cs
   public string? Description { get; set; }
   ```

3. **Add integration tests** to validate the migration. See `SchemaInitializerIntegrationTests.cs` for examples:

   ```csharp
   [TestMethod]
   public async Task EnsureSchema_V3Migration_AddsDescriptionColumnToGrants()
   {
       // ... setup ...
       await initializer.EnsureSchemaAsync();
       var hasColumn = await ColumnExistsAsync("SqlzibarGrants", "Description");
       Assert.IsTrue(hasColumn, "SqlzibarGrants.Description column should exist after v3 migration");
   }
   ```

That's it! No need to modify `SqlzibarSchemaInitializer.cs` - it auto-discovers all `NNN_*.sql` files in the Schema folder.

## Local Development Workflow

1. **Fresh database**: Run the app against an empty database. The initializer creates all tables from scratch.

2. **Existing database (previous version)**: Run the app against a database with the previous version. The initializer detects the version gap and runs upgrade scripts.

3. **Testing both paths**: Always test new scripts against:
   - A fresh database (no tables at all)
   - A database at the previous version (upgrade path)

4. **Docker SQL Server** (for integration tests):
   ```bash
   # Tests use Aspire to spin up SQL Server automatically
   dotnet test tests/Sqlzibar.IntegrationTests/
   ```

## Publishing Workflow

1. Bump the NuGet package version
2. Add release notes describing the schema changes
3. Consumers upgrading to the new package version get schema updates automatically on their next app startup — no `dotnet ef migrations add` needed
4. The upgrade is transparent: `UseSqlzibarAsync()` detects the version gap and applies pending scripts

## Script Placeholder Reference

| Placeholder              | Source                             |
|--------------------------|------------------------------------|
| `{Schema}`               | `SqlzibarOptions.Schema`           |
| `{SubjectTypes}`       | `SqlzibarOptions.TableNames.SubjectTypes` |
| `{Subjects}`           | `SqlzibarOptions.TableNames.Subjects` |
| `{UserGroups}`           | `SqlzibarOptions.TableNames.UserGroups` |
| `{UserGroupMemberships}` | `SqlzibarOptions.TableNames.UserGroupMemberships` |
| `{ResourceTypes}`        | `SqlzibarOptions.TableNames.ResourceTypes` |
| `{Resources}`            | `SqlzibarOptions.TableNames.Resources` |
| `{Grants}`               | `SqlzibarOptions.TableNames.Grants` |
| `{Roles}`                | `SqlzibarOptions.TableNames.Roles` |
| `{Permissions}`          | `SqlzibarOptions.TableNames.Permissions` |
| `{RolePermissions}`      | `SqlzibarOptions.TableNames.RolePermissions` |
| `{ServiceAccounts}`      | `SqlzibarOptions.TableNames.ServiceAccounts` |
