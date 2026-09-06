-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
-- SqlOSFga Schema v4: Query-composable authorization indexes
-- These indexes support the TVF plan used for resource ancestry walks and grant lookups.

CREATE INDEX IF NOT EXISTS "IX_{Resources}_ParentId"
    ON "{Schema}"."{Resources}"("ParentId");

CREATE INDEX IF NOT EXISTS "IX_{RolePermissions}_PermissionId_RoleId"
    ON "{Schema}"."{RolePermissions}"("PermissionId")
    INCLUDE ("RoleId");

CREATE INDEX IF NOT EXISTS "IX_{Grants}_ResourceId_SubjectId"
    ON "{Schema}"."{Grants}"("ResourceId")
    INCLUDE ("SubjectId", "RoleId", "EffectiveFrom", "EffectiveTo");

CREATE INDEX IF NOT EXISTS "IX_{Grants}_SubjectId"
    ON "{Schema}"."{Grants}"("SubjectId");

UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 4 WHERE "Version" < 4;
