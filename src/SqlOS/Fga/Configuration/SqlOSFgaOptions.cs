using Microsoft.AspNetCore.Http;
using SqlOS.Configuration;

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
/// Set <see cref="AuthMode"/> to <see cref="SqlOSDashboardAuthMode.Password"/> to enable
/// the built-in password login flow.
/// </summary>
public class SqlOSFgaDashboardOptions
{
    /// <summary>
    /// Controls how dashboard access is authenticated.
    /// </summary>
    public SqlOSDashboardAuthMode AuthMode { get; set; } = SqlOSDashboardAuthMode.DevelopmentOnly;

    /// <summary>
    /// Shared password used by the built-in dashboard login flow when <see cref="AuthMode"/>
    /// is set to <see cref="SqlOSDashboardAuthMode.Password"/>.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Session lifetime for the dashboard cookie issued after a successful password login.
    /// </summary>
    public TimeSpan SessionLifetime { get; set; } = SqlOSDashboardOptions.DefaultSessionLifetime;

    /// <summary>
    /// Custom authorization callback for dashboard access.
    /// Return true to allow access, false to deny. In password mode this callback runs
    /// after session validation so hosts can add additional checks.
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
