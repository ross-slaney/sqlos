-- PostgreSQL translation of the matching SQL Server script.
-- One SAML connection may bind a given user to at most one IdP subject.
-- Duplicate rows from earlier email-link races keep the oldest binding.

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSExternalIdentities')) IS NOT NULL
     AND EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = '{Schema}'
          AND table_name = 'SqlOSExternalIdentities'
          AND column_name = 'ConnectionId')
  THEN
    DELETE FROM "{Schema}"."SqlOSExternalIdentities" AS victim
    USING (
      SELECT "Id",
             ROW_NUMBER() OVER (
               PARTITION BY "ConnectionId", "UserId"
               ORDER BY "CreatedAt" ASC, "Id" ASC) AS row_number
      FROM "{Schema}"."SqlOSExternalIdentities"
      WHERE "ConnectionId" IS NOT NULL
    ) ranked
    WHERE victim."Id" = ranked."Id"
      AND ranked.row_number > 1;

    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSExternalIdentities_SsoConnectionId_UserId"
        ON "{Schema}"."SqlOSExternalIdentities"("ConnectionId", "UserId")
        WHERE "ConnectionId" IS NOT NULL;
  END IF;
END
$sqlos_guard$;
