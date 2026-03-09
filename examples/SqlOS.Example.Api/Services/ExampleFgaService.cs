using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Models;
using SqlOS.Example.Api.Data;
using SqlOS.Example.Api.Models;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Models;

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

    public async Task EnsureUserAccessAsync(string userId, string organizationId, CancellationToken cancellationToken = default)
    {
        var user = await _context.Set<SqlOSUser>()
            .FirstAsync(x => x.Id == userId, cancellationToken);
        var organization = await _context.Set<SqlOSOrganization>()
            .FirstAsync(x => x.Id == organizationId, cancellationToken);
        var membership = await _context.Set<SqlOSMembership>()
            .FirstAsync(x => x.UserId == userId && x.OrganizationId == organizationId && x.IsActive, cancellationToken);

        await EnsureSubjectAsync(user, organizationId, cancellationToken);
        await EnsureOrganizationResourceAsync(organization, cancellationToken);
        await EnsureMembershipGrantAsync(userId, organizationId, membership.Role, cancellationToken);
    }

    public string CreateWorkspaceResource(Workspace workspace)
    {
        var resourceId = GetWorkspaceResourceId(workspace.Id);
        _context.CreateResource(
            GetOrganizationResourceId(workspace.OrganizationId),
            workspace.Name,
            WorkspaceResourceTypeId,
            resourceId);
        return resourceId;
    }

    public static string GetOrganizationResourceId(string organizationId) => $"org::{organizationId}";

    public static string GetWorkspaceResourceId(string workspaceId) => $"wrk::{workspaceId}";

    private async Task EnsureSubjectAsync(SqlOSUser user, string organizationId, CancellationToken cancellationToken)
    {
        var subject = await _context.Set<SqlOSFgaSubject>()
            .FirstOrDefaultAsync(x => x.Id == user.Id, cancellationToken);
        if (subject == null)
        {
            _context.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject
            {
                Id = user.Id,
                SubjectTypeId = "user",
                DisplayName = user.DisplayName,
                OrganizationId = organizationId,
                ExternalRef = user.Id
            });
        }
        else
        {
            subject.DisplayName = user.DisplayName;
            subject.OrganizationId = organizationId;
            subject.ExternalRef = user.Id;
        }

        var fgaUser = await _context.Set<SqlOSFgaUser>()
            .FirstOrDefaultAsync(x => x.SubjectId == user.Id, cancellationToken);
        if (fgaUser == null)
        {
            _context.Set<SqlOSFgaUser>().Add(new SqlOSFgaUser
            {
                Id = BuildEntityId("fgausr", user.Id),
                SubjectId = user.Id,
                Email = user.DefaultEmail,
                IsActive = user.IsActive
            });
        }
        else
        {
            fgaUser.Email = user.DefaultEmail;
            fgaUser.IsActive = user.IsActive;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureOrganizationResourceAsync(SqlOSOrganization organization, CancellationToken cancellationToken)
    {
        var resourceId = GetOrganizationResourceId(organization.Id);
        var existing = await _context.Set<SqlOSFgaResource>()
            .FirstOrDefaultAsync(x => x.Id == resourceId, cancellationToken);
        if (existing == null)
        {
            _context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
            {
                Id = resourceId,
                ParentId = "root",
                Name = organization.Name,
                ResourceTypeId = OrganizationResourceTypeId,
                IsActive = organization.IsActive
            });
        }
        else
        {
            existing.Name = organization.Name;
            existing.IsActive = organization.IsActive;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureMembershipGrantAsync(
        string userId,
        string organizationId,
        string role,
        CancellationToken cancellationToken)
    {
        var resourceId = GetOrganizationResourceId(organizationId);
        var roleKey = IsElevatedRole(role) ? OrgAdminRole : OrgMemberRole;
        var roleEntity = await _context.Set<SqlOSFgaRole>()
            .FirstAsync(x => x.Key == roleKey, cancellationToken);

        var existingGrants = await _context.Set<SqlOSFgaGrant>()
            .Where(x => x.SubjectId == userId && x.ResourceId == resourceId)
            .ToListAsync(cancellationToken);

        foreach (var grant in existingGrants.Where(x => x.RoleId != roleEntity.Id))
        {
            _context.Set<SqlOSFgaGrant>().Remove(grant);
        }

        if (!existingGrants.Any(x => x.RoleId == roleEntity.Id))
        {
            _context.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant
            {
                Id = BuildEntityId("grant", $"{userId}_{organizationId}_{roleKey}"),
                SubjectId = userId,
                ResourceId = resourceId,
                RoleId = roleEntity.Id,
                Description = "Synced from auth membership."
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private static bool IsElevatedRole(string role)
        => role.Equals("admin", StringComparison.OrdinalIgnoreCase)
           || role.Equals("owner", StringComparison.OrdinalIgnoreCase);

    private static string BuildEntityId(string prefix, string source)
    {
        var sanitized = new string(source.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = Guid.NewGuid().ToString("N");
        }

        var value = $"{prefix}_{sanitized}";
        return value.Length <= 30 ? value : value[..30];
    }
}
