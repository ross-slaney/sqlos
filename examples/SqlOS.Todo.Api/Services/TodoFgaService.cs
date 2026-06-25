using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.Extensions;
using SqlOS.Fga.Models;
using SqlOS.Todo.Api.Data;
using SqlOS.Todo.Api.Models;

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

        var user = await _context.EnsureSqlOSUserSubjectAsync(
            subjectId,
            displayName,
            email,
            externalRef: subjectId,
            isActive: true,
            cancellationToken: cancellationToken);
        user.LastLoginAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.EnsureSqlOSResourceAsync(
            tenantResourceId,
            "root",
            displayName,
            TenantResourceTypeId,
            $"Todo tenant for {subjectId}",
            cancellationToken);
        await _context.EnsureSqlOSRoleGrantAsync(
            subjectId,
            tenantResourceId,
            TenantOwnerRole,
            "Todo tenant owner grant.",
            cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return new TodoFgaContext(subjectId, tenantResourceId);
    }

    public async Task<string> CreateTodoResourceAsync(
        TodoItem item,
        string tenantResourceId,
        CancellationToken cancellationToken = default)
    {
        var resourceId = GetTodoResourceId(item.Id);
        await _context.EnsureSqlOSResourceAsync(
            resourceId,
            tenantResourceId,
            item.Title,
            TodoResourceTypeId,
            cancellationToken: cancellationToken);
        return resourceId;
    }

    public async Task RemoveTodoResourceAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        var resource = await _context.Set<SqlOSFgaResource>()
            .FirstOrDefaultAsync(x => x.Id == resourceId, cancellationToken);
        if (resource != null)
        {
            _context.Set<SqlOSFgaResource>().Remove(resource);
        }
    }

    public static string GetTenantResourceId(string userId) => $"tenant::{userId}";

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
