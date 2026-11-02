ALTER TABLE IF EXISTS "{Schema}"."SqlOSUsers" ADD COLUMN IF NOT EXISTS "DeletedAt" timestamp NULL;
ALTER TABLE IF EXISTS "{Schema}"."SqlOSOrganizations" ADD COLUMN IF NOT EXISTS "DeletedAt" timestamp NULL;
