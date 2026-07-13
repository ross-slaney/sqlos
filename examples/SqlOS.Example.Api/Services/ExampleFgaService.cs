using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Models;
using SqlOS.Example.Api.Data;
using SqlOS.Extensions;

namespace SqlOS.Example.Api.Services;

public sealed class ExampleFgaService
{
    public const string OrganizationResourceTypeId = "organization";
    public const string WorkspaceResourceTypeId = "workspace";
    public const string WorkspaceViewPermission = "WORKSPACE_VIEW";
    public const string WorkspaceManagePermission = "WORKSPACE_MANAGE";
    public const string OrgMemberRole = "org_member";
    public const string OrgAdminRole = "org_admin";

    private readonly ExampleAppDbContext _context;

    public ExampleFgaService(ExampleAppDbContext context)
    {
        _context = context;
    }

    public async Task EnsureUserAccessAsync(string subjectId, string organizationId, CancellationToken cancellationToken = default)
    {
        var user = await _context.Set<SqlOSUser>()
            .FirstAsync(x => x.Id == subjectId, cancellationToken);
        var organization = await _context.Set<SqlOSOrganization>()
            .FirstAsync(x => x.Id == organizationId, cancellationToken);
        var membership = await _context.Set<SqlOSMembership>()
            .FirstAsync(x => x.UserId == subjectId && x.OrganizationId == organizationId && x.IsActive, cancellationToken);

        await ProvisionSubjectAsync(user, organizationId, cancellationToken);
        await ProvisionOrganizationResourceAsync(organization, cancellationToken);
        await SyncMembershipGrantAsync(subjectId, organizationId, membership.Role, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public static string GetOrganizationResourceId(string organizationId) => $"org::{organizationId}";

    public static string GetWorkspaceResourceId(string workspaceId) => $"wrk::{workspaceId}";

    private async Task ProvisionSubjectAsync(SqlOSUser user, string organizationId, CancellationToken cancellationToken)
    {
        await _context.ProvisionUserSubjectAsync(
            user.Id,
            user.DisplayName,
            user.DefaultEmail,
            organizationId,
            externalRef: user.Id,
            isActive: user.IsActive,
            cancellationToken: cancellationToken);
    }

    private async Task ProvisionOrganizationResourceAsync(SqlOSOrganization organization, CancellationToken cancellationToken)
        => await _context.ProvisionResourceWithIdAsync(
            GetOrganizationResourceId(organization.Id),
            OrganizationResourceTypeId,
            organization.Name,
            "root",
            isActive: organization.IsActive,
            cancellationToken: cancellationToken);

    private async Task SyncMembershipGrantAsync(
        string subjectId,
        string organizationId,
        string role,
        CancellationToken cancellationToken)
    {
        var resourceId = GetOrganizationResourceId(organizationId);
        var roleKey = IsElevatedRole(role) ? OrgAdminRole : OrgMemberRole;

        await _context.RevokeRoleAsync(
            subjectId,
            resourceId,
            roleKey == OrgAdminRole ? OrgMemberRole : OrgAdminRole,
            cancellationToken);
        await _context.GrantRoleAsync(subjectId, resourceId, roleKey, cancellationToken);
    }

    private static bool IsElevatedRole(string role)
        => role.Equals("admin", StringComparison.OrdinalIgnoreCase)
           || role.Equals("owner", StringComparison.OrdinalIgnoreCase);
}
