-- PostgreSQL translation of the matching SQL Server script.
-- SqlOS schema v48: an index on UserId for every table the user aggregate loads by user.
-- The aggregate loads each part it changes whole: every live credential, every email address
-- and every external identity of one account. Sign-in reads credentials and email addresses
-- this way, and claiming an address reads external identities. The phone number,
-- authenticator, recovery code and MFA override tables already had a UserId index; these
-- three did not, so each of those reads scanned the whole table and a sign-in grew slower
-- with every account. The EF model has always declared these indexes. Column checks keep
-- the script safe on incomplete historical schemas used by in-place upgrades.

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSCredentials')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSCredentials' AND column_name = 'UserId') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSCredentials_UserId"
        ON "{Schema}"."SqlOSCredentials"("UserId");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSUserEmails')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSUserEmails' AND column_name = 'UserId') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSUserEmails_UserId"
        ON "{Schema}"."SqlOSUserEmails"("UserId");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSExternalIdentities')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSExternalIdentities' AND column_name = 'UserId') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSExternalIdentities_UserId"
        ON "{Schema}"."SqlOSExternalIdentities"("UserId");
  END IF;
END
$sqlos_guard$;
