ALTER TABLE "{Schema}"."{Roles}" ADD COLUMN IF NOT EXISTS "DefinitionSource" varchar(16) NOT NULL DEFAULT 'application';
ALTER TABLE "{Schema}"."{Roles}" ADD COLUMN IF NOT EXISTS "OwnerResourceId" varchar(450) NULL;
ALTER TABLE "{Schema}"."{Roles}" ADD COLUMN IF NOT EXISTS "Revision" bigint NOT NULL DEFAULT 1;
ALTER TABLE "{Schema}"."{Roles}" ADD COLUMN IF NOT EXISTS "ArchivedAt" timestamp NULL;
ALTER TABLE "{Schema}"."{Permissions}" ADD COLUMN IF NOT EXISTS "AllowInCustomRoles" boolean NOT NULL DEFAULT false;
DO $sqlos$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_{Roles}_OwnerResource' AND conrelid = '"{Schema}"."{Roles}"'::regclass) THEN
        ALTER TABLE "{Schema}"."{Roles}" ADD CONSTRAINT "FK_{Roles}_OwnerResource" FOREIGN KEY ("OwnerResourceId") REFERENCES "{Schema}"."{Resources}"("Id");
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_{Roles}_Ownership' AND conrelid = '"{Schema}"."{Roles}"'::regclass) THEN
        ALTER TABLE "{Schema}"."{Roles}" ADD CONSTRAINT "CK_{Roles}_Ownership" CHECK (
            ("DefinitionSource" = 'application' AND "OwnerResourceId" IS NULL AND "ArchivedAt" IS NULL) OR
            ("DefinitionSource" = 'customer' AND "OwnerResourceId" IS NOT NULL AND "IsVirtual" = false));
    END IF;
END
$sqlos$;
CREATE INDEX IF NOT EXISTS "IX_{Roles}_OwnerResourceId" ON "{Schema}"."{Roles}"("OwnerResourceId");
UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 11 WHERE "Version" < 11;
