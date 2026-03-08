using Microsoft.AspNetCore.Http;

namespace Sqlzibar.Configuration;

/// <summary>
/// Configuration options for the Sqlzibar library.
/// </summary>
public class SqlzibarOptions
{
    public string Schema { get; set; } = "dbo";
    public string RootResourceId { get; set; } = "root";
    public string RootResourceName { get; set; } = "Root";
    public bool InitializeFunctions { get; set; } = true;
    public bool SeedCoreData { get; set; } = true;
    /// <summary>
    /// Optional path to a YAML schema file. If set, schema (resource types, permissions, roles) will be seeded from this file on startup.
    /// </summary>
    public string? SchemaYamlPath { get; set; }
    public string DashboardPathPrefix { get; set; } = "/sqlzibar";
    public SqlzibarDashboardOptions Dashboard { get; set; } = new();
    public SqlzibarTableNames TableNames { get; set; } = new();
}

/// <summary>
/// Options for the Sqlzibar dashboard.
/// By default, the dashboard is only accessible in the Development environment.
/// Set <see cref="AuthorizationCallback"/> to override this behavior.
/// </summary>
public class SqlzibarDashboardOptions
{
    /// <summary>
    /// Custom authorization callback for dashboard access.
    /// Return true to allow access, false to deny.
    /// When null (default), the dashboard is only accessible in the Development environment.
    /// </summary>
    /// <example>
    /// // Allow in any environment:
    /// options.Dashboard.AuthorizationCallback = _ => Task.FromResult(true);
    ///
    /// // Require a specific header:
    /// options.Dashboard.AuthorizationCallback = ctx =>
    ///     Task.FromResult(ctx.Request.Headers["X-Dashboard-Key"] == "my-secret");
    /// </example>
    public Func<HttpContext, Task<bool>>? AuthorizationCallback { get; set; }
}

/// <summary>
/// Configurable table names for Sqlzibar entities.
/// Used by the TVF SQL generation to reference the correct tables.
/// </summary>
public class SqlzibarTableNames
{
    public string SubjectTypes { get; set; } = "SqlzibarSubjectTypes";
    public string Subjects { get; set; } = "SqlzibarSubjects";
    public string UserGroups { get; set; } = "SqlzibarUserGroups";
    public string UserGroupMemberships { get; set; } = "SqlzibarUserGroupMemberships";
    public string ResourceTypes { get; set; } = "SqlzibarResourceTypes";
    public string Resources { get; set; } = "SqlzibarResources";
    public string Grants { get; set; } = "SqlzibarGrants";
    public string Roles { get; set; } = "SqlzibarRoles";
    public string Permissions { get; set; } = "SqlzibarPermissions";
    public string RolePermissions { get; set; } = "SqlzibarRolePermissions";
    public string ServiceAccounts { get; set; } = "SqlzibarServiceAccounts";
    public string Users { get; set; } = "SqlzibarUsers";
    public string Agents { get; set; } = "SqlzibarAgents";
}
