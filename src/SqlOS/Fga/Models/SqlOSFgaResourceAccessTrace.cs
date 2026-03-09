namespace SqlOS.Fga.Models;

/// <summary>
/// Complete trace of a resource access decision.
/// Explains how the authorization engine determined whether access is granted or denied.
/// </summary>
public class SqlOSFgaResourceAccessTrace
{
    public string TargetResourceId { get; set; } = string.Empty;
    public string TargetResourceName { get; set; } = string.Empty;
    public string TargetResourceType { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string SubjectDisplayName { get; set; } = string.Empty;
    public string PermissionKey { get; set; } = string.Empty;
    public string PermissionName { get; set; } = string.Empty;
    public bool AccessGranted { get; set; }
    public List<SqlOSFgaResourcePathNodeTrace> PathNodes { get; set; } = new();
    public List<SqlOSFgaRoleTrace> AllRolesUsed { get; set; } = new();
    public List<SqlOSFgaGrantTrace> GrantsUsed { get; set; } = new();
    public string DecisionSummary { get; set; } = string.Empty;
    public string? DenialReason { get; set; }
    public string? Suggestion { get; set; }
    public List<SqlOSFgaSubjectInfo> SubjectsChecked { get; set; } = new();
}

public class SqlOSFgaResourcePathNodeTrace
{
    public string ResourceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public int Depth { get; set; }
    public bool IsTarget { get; set; }
    public List<SqlOSFgaGrantTrace> GrantsOnThisNode { get; set; } = new();
    public List<string> EffectivePermissions { get; set; } = new();
    public bool PermissionFoundHere { get; set; }
}

public class SqlOSFgaGrantTrace
{
    public string GrantId { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string RoleKey { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string SubjectDisplayName { get; set; } = string.Empty;
    public bool AppliesToSubject { get; set; }
    public bool IsDirectGrant { get; set; }
    public string? ViaGroupName { get; set; }
    public bool ContributedToDecision { get; set; }
}

public class SqlOSFgaRoleTrace
{
    public string RoleKey { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public bool IsFromGrant { get; set; }
    public bool IsVirtualRole { get; set; }
    public string? SourceResourceId { get; set; }
    public string? SourceResourceName { get; set; }
    public string? SourceResourceType { get; set; }
    public List<SqlOSFgaPermissionAssignmentTrace> Permissions { get; set; } = new();
    public bool ContributedToDecision { get; set; }
}

public class SqlOSFgaPermissionAssignmentTrace
{
    public string PermissionKey { get; set; } = string.Empty;
    public string PermissionName { get; set; } = string.Empty;
    public bool UsedForDecision { get; set; }
}

public class SqlOSFgaSubjectInfo
{
    public string SubjectId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsDirect { get; set; }
}
