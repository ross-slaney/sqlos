-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
-- SqlOS schema v41: admin cursor-pagination indexes.
-- Each keyset order ends in a unique tiebreaker so dashboard lists can seek
-- without OFFSET or a full-result COUNT(*). Column checks keep the script
-- safe on incomplete historical schemas used by in-place upgrades.

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSOrganizations')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSOrganizations' AND column_name = 'Name') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSOrganizations' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSOrganizations_Name_Id"
        ON "{Schema}"."SqlOSOrganizations"("Name", "Id");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSUsers')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSUsers' AND column_name = 'DisplayName') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSUsers' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSUsers_DisplayName_Id"
        ON "{Schema}"."SqlOSUsers"("DisplayName", "Id");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSMemberships')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSMemberships' AND column_name = 'UserId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSMemberships' AND column_name = 'OrganizationId') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSMemberships_UserId"
        ON "{Schema}"."SqlOSMemberships"("UserId", "OrganizationId");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSSessions')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSessions' AND column_name = 'CreatedAt') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSessions' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSSessions_CreatedAt_Id"
        ON "{Schema}"."SqlOSSessions"("CreatedAt" DESC, "Id" DESC);
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSSessions')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSessions' AND column_name = 'UserId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSessions' AND column_name = 'CreatedAt') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSessions' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSSessions_UserId_CreatedAt_Id"
        ON "{Schema}"."SqlOSSessions"("UserId", "CreatedAt" DESC, "Id" DESC);
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSClientApplications')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientApplications' AND column_name = 'Name') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientApplications' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSClientApplications_Name_Id"
        ON "{Schema}"."SqlOSClientApplications"("Name", "Id");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuthOidcConnections')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuthOidcConnections' AND column_name = 'DisplayName') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuthOidcConnections' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSAuthOidcConnections_DisplayName_Id"
        ON "{Schema}"."SqlOSAuthOidcConnections"("DisplayName", "Id");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSSsoConnections')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSsoConnections' AND column_name = 'DisplayName') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSsoConnections' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSSsoConnections_DisplayName_Id"
        ON "{Schema}"."SqlOSSsoConnections"("DisplayName", "Id");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSSsoConnections')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSsoConnections' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSsoConnections' AND column_name = 'DisplayName') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSsoConnections' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSSsoConnections_OrganizationId_DisplayName_Id"
        ON "{Schema}"."SqlOSSsoConnections"("OrganizationId", "DisplayName", "Id");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSInvitations')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSInvitations' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSInvitations' AND column_name = 'CreatedAt') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSInvitations' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSInvitations_OrganizationId_CreatedAt_Id"
        ON "{Schema}"."SqlOSInvitations"("OrganizationId", "CreatedAt" DESC, "Id" DESC);
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSSsoPortalSessions')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSsoPortalSessions' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSsoPortalSessions' AND column_name = 'CreatedAt') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSsoPortalSessions' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSSsoPortalSessions_OrganizationId_CreatedAt_Id"
        ON "{Schema}"."SqlOSSsoPortalSessions"("OrganizationId", "CreatedAt" DESC, "Id" DESC);
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'OccurredAt') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'IngestedAt') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSAuditEvents_OccurredAt_IngestedAt_Id"
        ON "{Schema}"."SqlOSAuditEvents"("OccurredAt" DESC, "IngestedAt" DESC, "Id" DESC);
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSEmailTemplates')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailTemplates' AND column_name = 'Key') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailTemplates' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSEmailTemplates_Key_Id"
        ON "{Schema}"."SqlOSEmailTemplates"("Key", "Id");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSEmailDeliveries')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailDeliveries' AND column_name = 'CreatedAt') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailDeliveries' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSEmailDeliveries_CreatedAt_Id"
        ON "{Schema}"."SqlOSEmailDeliveries"("CreatedAt" DESC, "Id" DESC);
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSCalendarConnections')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSCalendarConnections' AND column_name = 'CreatedAt') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSCalendarConnections' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSCalendarConnections_CreatedAt_Id"
        ON "{Schema}"."SqlOSCalendarConnections"("CreatedAt" DESC, "Id" DESC);
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSApplicationAssignments')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSApplicationAssignments' AND column_name = 'ClientApplicationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSApplicationAssignments' AND column_name = 'CreatedAt') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSApplicationAssignments' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSApplicationAssignments_Client_CreatedAt_Id"
        ON "{Schema}"."SqlOSApplicationAssignments"("ClientApplicationId", "CreatedAt" DESC, "Id" DESC);
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSClientCredentials')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientCredentials' AND column_name = 'ClientApplicationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientCredentials' AND column_name = 'CreatedAt') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientCredentials' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSClientCredentials_Client_CreatedAt_Id"
        ON "{Schema}"."SqlOSClientCredentials"("ClientApplicationId", "CreatedAt" DESC, "Id" DESC);
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimConnections')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimConnections' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimConnections' AND column_name = 'DisplayName') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimConnections' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSScimConnections_OrganizationId_DisplayName_Id"
        ON "{Schema}"."SqlOSScimConnections"("OrganizationId", "DisplayName", "Id");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimGroupMappings')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimGroupMappings' AND column_name = 'ConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimGroupMappings' AND column_name = 'CreatedAt') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimGroupMappings' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSScimGroupMappings_ConnectionId_CreatedAt_Id"
        ON "{Schema}"."SqlOSScimGroupMappings"("ConnectionId", "CreatedAt" DESC, "Id" DESC);
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimSyncEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimSyncEvents' AND column_name = 'ConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimSyncEvents' AND column_name = 'OccurredAt') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimSyncEvents' AND column_name = 'Id') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSScimSyncEvents_ConnectionId_OccurredAt_Id"
        ON "{Schema}"."SqlOSScimSyncEvents"("ConnectionId", "OccurredAt" DESC, "Id" DESC);
  END IF;
END
$sqlos_guard$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (41);
