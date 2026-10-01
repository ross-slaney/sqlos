CREATE OR ALTER FUNCTION [authz].fn_IsResourceAccessible(
    @ResourceId NVARCHAR(128),
    @SubjectIds NVARCHAR(MAX),
    @PermissionId NVARCHAR(128)
)
RETURNS TABLE
AS
RETURN
(
    WITH ancestors AS (
        SELECT
            Id,
            ParentId,
            0 AS Depth,
            CAST(N'|' + Id + N'|' AS NVARCHAR(MAX)) AS VisitedPath,
            CAST(0 AS BIT) AS CycleDetected
        FROM [authz].[AppResources]
        WHERE Id = @ResourceId AND IsActive = 1

        UNION ALL

        SELECT
            r.Id,
            r.ParentId,
            a.Depth + 1,
            CAST(a.VisitedPath + r.Id + N'|' AS NVARCHAR(MAX)),
            CAST(CASE
                WHEN CHARINDEX(N'|' + r.Id + N'|', a.VisitedPath) > 0 THEN 1
                ELSE 0
            END AS BIT)
        FROM [authz].[AppResources] r
        INNER JOIN ancestors a ON r.Id = a.ParentId
        WHERE a.Depth < 25
          AND a.CycleDetected = 0
          AND r.IsActive = 1
    )
    SELECT TOP 1 a.Id
    FROM ancestors a
    INNER JOIN [authz].[AppGrants] g ON a.Id = g.ResourceId
    INNER JOIN [authz].[AppRolePermissions] rp ON g.RoleId = rp.RoleId
    INNER JOIN [authz].[AppSubjects] s ON g.SubjectId = s.Id
    LEFT JOIN [authz].[AppUsers] u ON s.Id = u.SubjectId
    LEFT JOIN [authz].[AppServiceAccounts] sa ON s.Id = sa.SubjectId
    LEFT JOIN [authz].[AppUserGroups] ug ON s.Id = ug.SubjectId
    LEFT JOIN [authz].[AppAgents] ag ON s.Id = ag.SubjectId
    WHERE g.SubjectId IN (SELECT CONVERT(NVARCHAR(450), [value]) FROM OPENJSON(@SubjectIds))
      AND rp.PermissionId = @PermissionId
      AND NOT EXISTS (SELECT 1 FROM ancestors malformed WHERE malformed.CycleDetected = 1)
      AND NOT EXISTS (
          SELECT 1
          FROM ancestors truncated
          WHERE truncated.Depth = 25
            AND truncated.ParentId IS NOT NULL
      )
      AND EXISTS (
          SELECT 1
          FROM [authz].[AppResources] target
          INNER JOIN [authz].[AppPermissions] permission ON permission.Id = @PermissionId
          WHERE target.Id = @ResourceId
            AND (permission.ResourceTypeId IS NULL OR permission.ResourceTypeId = target.ResourceTypeId)
      )
      AND (s.SubjectTypeId <> 'user' OR u.IsActive = 1)
      AND (s.SubjectTypeId <> 'service_account' OR (sa.SubjectId IS NOT NULL AND (sa.ExpiresAt IS NULL OR sa.ExpiresAt > GETUTCDATE())))
      AND (s.SubjectTypeId <> 'group' OR ug.IsActive = 1)
      AND (s.SubjectTypeId <> 'agent' OR ag.SubjectId IS NOT NULL)
      AND EXISTS (
          SELECT 1
          FROM [authz].[AppSubjects] caller
          LEFT JOIN [authz].[AppUsers] callerUser ON caller.Id = callerUser.SubjectId
          LEFT JOIN [authz].[AppServiceAccounts] callerSa ON caller.Id = callerSa.SubjectId
          LEFT JOIN [authz].[AppUserGroups] callerGroup ON caller.Id = callerGroup.SubjectId
          LEFT JOIN [authz].[AppAgents] callerAgent ON caller.Id = callerAgent.SubjectId
          WHERE caller.Id = JSON_VALUE(@SubjectIds, '$[0]')
            AND (caller.SubjectTypeId <> 'user' OR callerUser.IsActive = 1)
            AND (caller.SubjectTypeId <> 'service_account' OR (callerSa.SubjectId IS NOT NULL AND (callerSa.ExpiresAt IS NULL OR callerSa.ExpiresAt > GETUTCDATE())))
            AND (caller.SubjectTypeId <> 'group' OR callerGroup.IsActive = 1)
            AND (caller.SubjectTypeId <> 'agent' OR callerAgent.SubjectId IS NOT NULL)
      )
      AND (g.EffectiveFrom IS NULL OR g.EffectiveFrom <= GETUTCDATE())
      AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= GETUTCDATE())
)