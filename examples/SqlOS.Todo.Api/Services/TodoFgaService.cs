using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.Fga.Extensions;
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
        var hasChanges = false;

        hasChanges |= await UpsertSubjectAsync(subjectId, displayName, cancellationToken);
        hasChanges |= await UpsertUserAsync(subjectId, email, cancellationToken);
        hasChanges |= await UpsertTenantResourceAsync(subjectId, tenantResourceId, displayName, cancellationToken);
        hasChanges |= await UpsertTenantGrantAsync(subjectId, tenantResourceId, cancellationToken);

        if (hasChanges)
        {
            await _context.SaveChangesAsync(cancellationToken);
        }

        return new TodoFgaContext(subjectId, tenantResourceId);
    }

    public string CreateTodoResource(TodoItem item, string tenantResourceId)
    {
        var resourceId = GetTodoResourceId(item.Id);
        _context.CreateResource(tenantResourceId, item.Title, TodoResourceTypeId, resourceId);
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

    private async Task<bool> UpsertSubjectAsync(
        string subjectId,
        string displayName,
        CancellationToken cancellationToken)
    {
        var subject = await _context.Set<SqlOSFgaSubject>()
            .FirstOrDefaultAsync(x => x.Id == subjectId, cancellationToken);

        if (subject == null)
        {
            _context.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject
            {
                Id = subjectId,
                SubjectTypeId = "user",
                DisplayName = displayName,
                ExternalRef = subjectId
            });
            return true;
        }

        var changed = false;
        if (!string.Equals(subject.SubjectTypeId, "user", StringComparison.Ordinal))
        {
            subject.SubjectTypeId = "user";
            changed = true;
        }

        if (!string.Equals(subject.DisplayName, displayName, StringComparison.Ordinal))
        {
            subject.DisplayName = displayName;
            changed = true;
        }

        if (!string.Equals(subject.ExternalRef, subjectId, StringComparison.Ordinal))
        {
            subject.ExternalRef = subjectId;
            changed = true;
        }

        if (changed)
        {
            subject.UpdatedAt = DateTime.UtcNow;
        }

        return changed;
    }

    private async Task<bool> UpsertUserAsync(
        string subjectId,
        string? email,
        CancellationToken cancellationToken)
    {
        var user = await _context.Set<SqlOSFgaUser>()
            .FirstOrDefaultAsync(x => x.SubjectId == subjectId, cancellationToken);
        if (user == null)
        {
            _context.Set<SqlOSFgaUser>().Add(new SqlOSFgaUser
            {
                Id = $"fgauser::{subjectId}",
                SubjectId = subjectId,
                Email = email,
                IsActive = true,
                LastLoginAt = DateTime.UtcNow
            });
            return true;
        }

        if (!string.Equals(user.Email, email, StringComparison.Ordinal))
        {
            user.Email = email;
        }

        if (!user.IsActive)
        {
            user.IsActive = true;
        }

        user.LastLoginAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    private async Task<bool> UpsertTenantResourceAsync(
        string subjectId,
        string tenantResourceId,
        string displayName,
        CancellationToken cancellationToken)
    {
        var resource = await _context.Set<SqlOSFgaResource>()
            .FirstOrDefaultAsync(x => x.Id == tenantResourceId, cancellationToken);
        if (resource == null)
        {
            _context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
            {
                Id = tenantResourceId,
                ParentId = "root",
                Name = displayName,
                Description = $"Todo tenant for {subjectId}",
                ResourceTypeId = TenantResourceTypeId,
                IsActive = true
            });
            return true;
        }

        var changed = false;
        if (!string.Equals(resource.ParentId, "root", StringComparison.Ordinal))
        {
            resource.ParentId = "root";
            changed = true;
        }

        if (!string.Equals(resource.Name, displayName, StringComparison.Ordinal))
        {
            resource.Name = displayName;
            changed = true;
        }

        var description = $"Todo tenant for {subjectId}";
        if (!string.Equals(resource.Description, description, StringComparison.Ordinal))
        {
            resource.Description = description;
            changed = true;
        }

        if (!string.Equals(resource.ResourceTypeId, TenantResourceTypeId, StringComparison.Ordinal))
        {
            resource.ResourceTypeId = TenantResourceTypeId;
            changed = true;
        }

        if (!resource.IsActive)
        {
            resource.IsActive = true;
            changed = true;
        }

        if (changed)
        {
            resource.UpdatedAt = DateTime.UtcNow;
        }

        return changed;
    }

    private async Task<bool> UpsertTenantGrantAsync(
        string subjectId,
        string tenantResourceId,
        CancellationToken cancellationToken)
    {
        var roleId = await _context.Set<SqlOSFgaRole>()
            .Where(x => x.Key == TenantOwnerRole)
            .Select(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(roleId))
        {
            throw new InvalidOperationException($"FGA role '{TenantOwnerRole}' was not seeded.");
        }

        var existingGrants = await _context.Set<SqlOSFgaGrant>()
            .Where(x => x.SubjectId == subjectId && x.ResourceId == tenantResourceId)
            .ToListAsync(cancellationToken);

        var changed = false;
        foreach (var grant in existingGrants.Where(x => x.RoleId != roleId))
        {
            _context.Set<SqlOSFgaGrant>().Remove(grant);
            changed = true;
        }

        var ownerGrant = existingGrants.FirstOrDefault(x => x.RoleId == roleId);
        if (ownerGrant == null)
        {
            _context.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant
            {
                Id = $"grant::{subjectId}::{TenantOwnerRole}",
                SubjectId = subjectId,
                ResourceId = tenantResourceId,
                RoleId = roleId,
                Description = "Todo tenant owner grant."
            });
            return true;
        }

        if (!string.Equals(ownerGrant.Description, "Todo tenant owner grant.", StringComparison.Ordinal))
        {
            ownerGrant.Description = "Todo tenant owner grant.";
            ownerGrant.UpdatedAt = DateTime.UtcNow;
            changed = true;
        }

        return changed;
    }

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
