using Microsoft.AspNetCore.Http;
using SqlOS.Configuration;
using SqlOS.Fga.Services;

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
    public SqlOSDashboardOptions Dashboard { get; set; } = new();
    public SqlOSFgaTableNames TableNames { get; set; } = new();
    public SqlOSFgaSeedData? StartupSeedData { get; private set; }

    /// <summary>
    /// Controls automatic index conventions and startup validation for consumer entities
    /// that implement <see cref="SqlOS.Fga.Interfaces.IHasResourceId"/>.
    /// </summary>
    public SqlOSFgaConventionsOptions Conventions { get; set; } = new();

    public SqlOSFgaOptions Seed(Action<SqlOSFgaSeedBuilder> configure)
    {
        var builder = StartupSeedData == null
            ? new SqlOSFgaSeedBuilder()
            : new SqlOSFgaSeedBuilder(StartupSeedData);
        configure(builder);
        StartupSeedData = builder.Build();
        return this;
    }
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
