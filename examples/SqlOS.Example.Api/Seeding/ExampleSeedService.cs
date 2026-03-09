using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Example.Api.Configuration;
using SqlOS.Example.Api.Data;
using SqlOS.Example.Api.Services;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;

namespace SqlOS.Example.Api.Seeding;

public sealed class ExampleSeedService
{
    private readonly ExampleAppDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSFgaSeedService _fgaSeedService;
    private readonly ExampleWebOptions _webOptions;

    public ExampleSeedService(
        ExampleAppDbContext context,
        SqlOSAdminService adminService,
        SqlOSFgaSeedService fgaSeedService,
        IOptions<ExampleWebOptions> webOptions)
    {
        _context = context;
        _adminService = adminService;
        _fgaSeedService = fgaSeedService;
        _webOptions = webOptions.Value;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizationSchemaAsync(cancellationToken);

        var redirectUris = new List<string> { _webOptions.CallbackUrl, "https://client.example.local/callback" }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existing = await _context.Set<SqlOSClientApplication>()
            .FirstOrDefaultAsync(x => x.ClientId == _webOptions.ClientId, cancellationToken);

        if (existing == null)
        {
            await _adminService.CreateClientAsync(new SqlOSCreateClientRequest(
                _webOptions.ClientId,
                "Example Web Client",
                "sqlos-example",
                redirectUris), cancellationToken);
            return;
        }

        existing.Name = "Example Web Client";
        existing.Audience = "sqlos-example";
        existing.IsActive = true;
        existing.RedirectUrisJson = JsonSerializer.Serialize(redirectUris);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureAuthorizationSchemaAsync(CancellationToken cancellationToken)
    {
        await _fgaSeedService.SeedAuthorizationDataAsync(new SqlOSFgaSeedData
        {
            ResourceTypes =
            [
                new SqlOSFgaResourceType
                {
                    Id = ExampleFgaService.OrganizationResourceTypeId,
                    Name = "Organization",
                    Description = "Organization root for example workspace access."
                },
                new SqlOSFgaResourceType
                {
                    Id = ExampleFgaService.WorkspaceResourceTypeId,
                    Name = "Workspace",
                    Description = "Workspace resources in the example application."
                }
            ],
            Permissions =
            [
                new SqlOSFgaPermission
                {
                    Id = "perm_workspace_view",
                    Key = ExampleFgaService.WorkspaceViewPermission,
                    Name = "View workspaces",
                    ResourceTypeId = ExampleFgaService.WorkspaceResourceTypeId
                },
                new SqlOSFgaPermission
                {
                    Id = "perm_workspace_manage",
                    Key = ExampleFgaService.WorkspaceManagePermission,
                    Name = "Manage workspaces",
                    ResourceTypeId = ExampleFgaService.WorkspaceResourceTypeId
                }
            ],
            Roles =
            [
                new SqlOSFgaRole
                {
                    Id = "role_org_member",
                    Key = ExampleFgaService.OrgMemberRole,
                    Name = "Organization Member"
                },
                new SqlOSFgaRole
                {
                    Id = "role_org_admin",
                    Key = ExampleFgaService.OrgAdminRole,
                    Name = "Organization Admin"
                }
            ],
            RolePermissions =
            [
                (ExampleFgaService.OrgMemberRole, new[] { ExampleFgaService.WorkspaceViewPermission }),
                (ExampleFgaService.OrgAdminRole, new[] { ExampleFgaService.WorkspaceViewPermission, ExampleFgaService.WorkspaceManagePermission })
            ]
        }, cancellationToken);
    }
}
