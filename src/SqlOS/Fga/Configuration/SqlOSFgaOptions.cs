using Microsoft.AspNetCore.Http;

namespace SqlOS.Fga.Configuration;

/// <summary>
/// Configuration options for the SqlOSFga library.
/// </summary>
public class SqlOSFgaOptions
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
    public string DashboardPathPrefix { get; set; } = "/sqlos/admin/fga";
    public SqlOSFgaDashboardOptions Dashboard { get; set; } = new();
    public SqlOSFgaTableNames TableNames { get; set; } = new();
}

/// <summary>
/// Options for the SqlOSFga dashboard.
/// By default, the dashboard is only accessible in the Development environment.
/// Set <see cref="AuthorizationCallback"/> to override this behavior.
/// </summary>
public class SqlOSFgaDashboardOptions
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
/// Configurable table names for SqlOSFga entities.
/// Used by the TVF SQL generation to reference the correct tables.
/// </summary>
public class SqlOSFgaTableNames
{
    public string SubjectTypes { get; set; } = "SqlOSFgaSubjectTypes";
    public string Subjects { get; set; } = "SqlOSFgaSubjects";
    public string UserGroups { get; set; } = "SqlOSFgaUserGroups";
    public string UserGroupMemberships { get; set; } = "SqlOSFgaUserGroupMemberships";
    public string ResourceTypes { get; set; } = "SqlOSFgaResourceTypes";
    public string Resources { get; set; } = "SqlOSFgaResources";
    public string Grants { get; set; } = "SqlOSFgaGrants";
    public string Roles { get; set; } = "SqlOSFgaRoles";
    public string Permissions { get; set; } = "SqlOSFgaPermissions";
    public string RolePermissions { get; set; } = "SqlOSFgaRolePermissions";
    public string ServiceAccounts { get; set; } = "SqlOSFgaServiceAccounts";
    public string Users { get; set; } = "SqlOSFgaUsers";
    public string Agents { get; set; } = "SqlOSFgaAgents";
}
