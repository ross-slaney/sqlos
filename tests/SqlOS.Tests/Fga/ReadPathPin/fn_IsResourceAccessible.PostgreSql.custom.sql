CREATE OR REPLACE FUNCTION "authz"."fn_IsResourceAccessible"(
    p_resource_id varchar(128),
    p_subject_ids text,
    p_permission_id varchar(128)
)
RETURNS TABLE("Id" varchar(450))
LANGUAGE sql
STABLE
AS $sqlos$
WITH RECURSIVE ancestors AS (
    SELECT
        "Id",
        "ParentId",
        0 AS "Depth",
        ('|' || "Id" || '|')::text AS "VisitedPath",
        FALSE AS "CycleDetected"
    FROM "authz"."AppResources"
    WHERE "Id" = p_resource_id AND "IsActive" = TRUE

    UNION ALL

    SELECT
        r."Id",
        r."ParentId",
        a."Depth" + 1,
        (a."VisitedPath" || r."Id" || '|')::text,
        (strpos(a."VisitedPath", '|' || r."Id" || '|') > 0)
    FROM "authz"."AppResources" r
    INNER JOIN ancestors a ON r."Id" = a."ParentId"
    WHERE a."Depth" < 25
      AND a."CycleDetected" = FALSE
      AND r."IsActive" = TRUE
)
SELECT a."Id"
FROM ancestors a
INNER JOIN "authz"."AppGrants" g ON a."Id" = g."ResourceId"
INNER JOIN "authz"."AppRolePermissions" rp ON g."RoleId" = rp."RoleId"
INNER JOIN "authz"."AppSubjects" s ON g."SubjectId" = s."Id"
LEFT JOIN "authz"."AppUsers" u ON s."Id" = u."SubjectId"
LEFT JOIN "authz"."AppServiceAccounts" sa ON s."Id" = sa."SubjectId"
LEFT JOIN "authz"."AppUserGroups" ug ON s."Id" = ug."SubjectId"
LEFT JOIN "authz"."AppAgents" ag ON s."Id" = ag."SubjectId"
WHERE g."SubjectId" IN (SELECT jsonb_array_elements_text(p_subject_ids::jsonb))
  AND rp."PermissionId" = p_permission_id
  AND NOT EXISTS (SELECT 1 FROM ancestors malformed WHERE malformed."CycleDetected" = TRUE)
  AND NOT EXISTS (
      SELECT 1
      FROM ancestors truncated
      WHERE truncated."Depth" = 25
        AND truncated."ParentId" IS NOT NULL
  )
  AND EXISTS (
      SELECT 1
      FROM "authz"."AppResources" target
      INNER JOIN "authz"."AppPermissions" permission ON permission."Id" = p_permission_id
      WHERE target."Id" = p_resource_id
        AND (permission."ResourceTypeId" IS NULL OR permission."ResourceTypeId" = target."ResourceTypeId")
  )
  AND (s."SubjectTypeId" <> 'user' OR u."IsActive" = TRUE)
  AND (s."SubjectTypeId" <> 'service_account' OR (sa."SubjectId" IS NOT NULL AND (sa."ExpiresAt" IS NULL OR sa."ExpiresAt" > (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'))))
  AND (s."SubjectTypeId" <> 'group' OR ug."IsActive" = TRUE)
  AND (s."SubjectTypeId" <> 'agent' OR ag."SubjectId" IS NOT NULL)
  AND EXISTS (
      SELECT 1
      FROM "authz"."AppSubjects" caller
      LEFT JOIN "authz"."AppUsers" callerUser ON caller."Id" = callerUser."SubjectId"
      LEFT JOIN "authz"."AppServiceAccounts" callerSa ON caller."Id" = callerSa."SubjectId"
      LEFT JOIN "authz"."AppUserGroups" callerGroup ON caller."Id" = callerGroup."SubjectId"
      LEFT JOIN "authz"."AppAgents" callerAgent ON caller."Id" = callerAgent."SubjectId"
      WHERE caller."Id" = (p_subject_ids::jsonb ->> 0)
        AND (caller."SubjectTypeId" <> 'user' OR callerUser."IsActive" = TRUE)
        AND (caller."SubjectTypeId" <> 'service_account' OR (callerSa."SubjectId" IS NOT NULL AND (callerSa."ExpiresAt" IS NULL OR callerSa."ExpiresAt" > (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'))))
        AND (caller."SubjectTypeId" <> 'group' OR callerGroup."IsActive" = TRUE)
        AND (caller."SubjectTypeId" <> 'agent' OR callerAgent."SubjectId" IS NOT NULL)
  )
  AND (g."EffectiveFrom" IS NULL OR g."EffectiveFrom" <= (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'))
  AND (g."EffectiveTo" IS NULL OR g."EffectiveTo" >= (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'))
LIMIT 1
$sqlos$;