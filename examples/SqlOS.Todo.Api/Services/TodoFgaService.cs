using System.Security.Claims;
using SqlOS.AuthServer.Contracts;
using SqlOS.Extensions;
using SqlOS.Todo.Api.Data;

namespace SqlOS.Todo.Api.Services;

public sealed class TodoFgaService
{
    public const string TenantResourceTypeId = "tenant";
    public const string TodoResourceTypeId = "todo";
    public const string TenantOwnerRole = "tenant_owner";
    public const string TenantCreateTodoPermission = "TENANT_CREATE_TODO";
    public const string TodoReadPermission = "TODO_READ";
    public const string TodoWritePermission = "TODO_WRITE";

    private readonly TodoSampleDbContext _context;

    public TodoFgaService(TodoSampleDbContext context)
    {
        _context = context;
    }

    public async Task<TodoFgaContext> EnsureUserTenantAccessAsync(
        SqlOSValidatedToken validated,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(validated.UserId))
        {
            throw new InvalidOperationException("Validated token must include a user id.");
        }

        var subjectId = validated.UserId;
        var displayName = GetDisplayName(validated.Principal, subjectId);
        var email = GetClaimValue(validated.Principal, "email");
        var tenantResourceId = GetTenantResourceId(subjectId);

        var user = await _context.ProvisionUserSubjectAsync(
            subjectId,
            displayName,
            email,
            externalRef: subjectId,
            isActive: true,
            cancellationToken: cancellationToken);
        user.LastLoginAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.ProvisionResourceWithIdAsync(
            tenantResourceId,
            TenantResourceTypeId,
            displayName,
            "root",
            $"Todo tenant for {subjectId}",
            isActive: true,
            cancellationToken);
        await _context.GrantRoleAsync(
            subjectId,
            tenantResourceId,
            TenantOwnerRole,
            cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return new TodoFgaContext(subjectId, tenantResourceId);
    }

    public static string GetTenantResourceId(string subjectId) => $"tenant::{subjectId}";

    public static string GetTodoResourceId(Guid todoId) => $"todo::{todoId:D}";

    private static string GetDisplayName(ClaimsPrincipal principal, string subjectId)
    {
        var displayName = GetClaimValue(principal, "name")
            ?? principal.Identity?.Name
            ?? GetClaimValue(principal, "email");

        return string.IsNullOrWhiteSpace(displayName) ? subjectId : displayName.Trim();
    }

    private static string? GetClaimValue(ClaimsPrincipal principal, string claimType)
        => principal.Claims.FirstOrDefault(x => x.Type == claimType)?.Value;
}

public sealed record TodoFgaContext(string SubjectId, string TenantResourceId);
