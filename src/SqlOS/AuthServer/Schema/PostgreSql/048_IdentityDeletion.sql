ALTER TABLE "{Schema}"."SqlOSUsers" ADD COLUMN IF NOT EXISTS "DeletedAt" timestamp NULL;
ALTER TABLE "{Schema}"."SqlOSOrganizations" ADD COLUMN IF NOT EXISTS "DeletedAt" timestamp NULL;
