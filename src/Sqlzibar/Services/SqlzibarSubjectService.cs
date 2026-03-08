using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sqlzibar.Interfaces;
using Sqlzibar.Models;

namespace Sqlzibar.Services;

public class SqlzibarSubjectService : ISqlzibarSubjectService
{
    private readonly ISqlzibarDbContext _context;
    private readonly ILogger<SqlzibarSubjectService> _logger;

    public SqlzibarSubjectService(
        ISqlzibarDbContext context,
        ILogger<SqlzibarSubjectService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<SqlzibarSubject> CreateSubjectAsync(
        string displayName,
        string subjectTypeId,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default)
    {
        var subject = new SqlzibarSubject
        {
            Id = $"subj_{Guid.NewGuid():N}"[..30],
            SubjectTypeId = subjectTypeId,
            DisplayName = displayName,
            OrganizationId = organizationId,
            ExternalRef = externalRef,
        };

        _context.Set<SqlzibarSubject>().Add(subject);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created subject {SubjectId} ({DisplayName}) of type {Type}",
            subject.Id, displayName, subjectTypeId);

        return subject;
    }

    public async Task<SqlzibarUserGroup> CreateGroupAsync(
        string name,
        string? description = null,
        string? groupType = null,
        CancellationToken cancellationToken = default)
    {
        var subject = await CreateSubjectAsync(name, "group", cancellationToken: cancellationToken);

        var group = new SqlzibarUserGroup
        {
            Id = $"grp_{Guid.NewGuid():N}"[..30],
            Name = name,
            Description = description,
            GroupType = groupType,
            SubjectId = subject.Id,
        };

        _context.Set<SqlzibarUserGroup>().Add(group);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created group {GroupId} ({Name})", group.Id, name);

        return group;
    }

    public async Task<SqlzibarUser> CreateUserAsync(
        string displayName,
        string? email = null,
        bool isActive = true,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default)
    {
        var subject = await CreateSubjectAsync(displayName, "user", organizationId, externalRef, cancellationToken);

        var user = new SqlzibarUser
        {
            Id = $"usr_{Guid.NewGuid():N}"[..30],
            SubjectId = subject.Id,
            Email = email,
            IsActive = isActive,
        };

        _context.Set<SqlzibarUser>().Add(user);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created user {UserId} ({DisplayName})", user.Id, displayName);

        return user;
    }

    public async Task<SqlzibarAgent> CreateAgentAsync(
        string displayName,
        string? agentType = null,
        string? description = null,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default)
    {
        var subject = await CreateSubjectAsync(displayName, "agent", organizationId, externalRef, cancellationToken);

        var agent = new SqlzibarAgent
        {
            Id = $"agt_{Guid.NewGuid():N}"[..30],
            SubjectId = subject.Id,
            AgentType = agentType,
            Description = description,
        };

        _context.Set<SqlzibarAgent>().Add(agent);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created agent {AgentId} ({DisplayName})", agent.Id, displayName);

        return agent;
    }

    public async Task<SqlzibarServiceAccount> CreateServiceAccountAsync(
        string displayName,
        string clientId,
        string clientSecretHash,
        string? description = null,
        DateTime? expiresAt = null,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default)
    {
        var subject = await CreateSubjectAsync(displayName, "service_account", organizationId, externalRef, cancellationToken);

        var serviceAccount = new SqlzibarServiceAccount
        {
            Id = $"sa_{Guid.NewGuid():N}"[..30],
            SubjectId = subject.Id,
            ClientId = clientId,
            ClientSecretHash = clientSecretHash,
            Description = description,
            ExpiresAt = expiresAt,
        };

        _context.Set<SqlzibarServiceAccount>().Add(serviceAccount);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created service account {ServiceAccountId} ({DisplayName})", serviceAccount.Id, displayName);

        return serviceAccount;
    }

    public async Task AddToGroupAsync(string subjectId, string userGroupId, CancellationToken cancellationToken = default)
    {
        // Validate subject is NOT a group type (no nested groups allowed)
        var subject = await _context.Set<SqlzibarSubject>()
            .FirstOrDefaultAsync(s => s.Id == subjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Subject '{subjectId}' not found");

        if (subject.SubjectTypeId == "group")
        {
            throw new InvalidOperationException("Groups cannot be members of other groups");
        }

        var exists = await _context.Set<SqlzibarUserGroupMembership>()
            .AnyAsync(m => m.SubjectId == subjectId && m.UserGroupId == userGroupId, cancellationToken);

        if (exists) return;

        var membership = new SqlzibarUserGroupMembership
        {
            SubjectId = subjectId,
            UserGroupId = userGroupId,
        };

        _context.Set<SqlzibarUserGroupMembership>().Add(membership);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Added subject {SubjectId} to group {GroupId}", subjectId, userGroupId);
    }

    public async Task RemoveFromGroupAsync(string subjectId, string userGroupId, CancellationToken cancellationToken = default)
    {
        var membership = await _context.Set<SqlzibarUserGroupMembership>()
            .FirstOrDefaultAsync(m => m.SubjectId == subjectId && m.UserGroupId == userGroupId, cancellationToken);

        if (membership == null) return;

        _context.Set<SqlzibarUserGroupMembership>().Remove(membership);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Removed subject {SubjectId} from group {GroupId}", subjectId, userGroupId);
    }

    public async Task<List<string>> ResolveSubjectIdsAsync(string subjectId, CancellationToken cancellationToken = default)
    {
        var subjects = new List<string> { subjectId };

        var groupSubjectIds = await _context.Set<SqlzibarUserGroupMembership>()
            .Where(m => m.SubjectId == subjectId)
            .Join(_context.Set<SqlzibarUserGroup>(),
                m => m.UserGroupId,
                g => g.Id,
                (m, g) => g.SubjectId)
            .ToListAsync(cancellationToken);

        subjects.AddRange(groupSubjectIds);
        return subjects;
    }

    public async Task<List<SqlzibarUserGroup>> GetGroupsForSubjectAsync(string subjectId, CancellationToken cancellationToken = default)
    {
        return await _context.Set<SqlzibarUserGroupMembership>()
            .Where(m => m.SubjectId == subjectId)
            .Join(_context.Set<SqlzibarUserGroup>(),
                m => m.UserGroupId,
                g => g.Id,
                (m, g) => g)
            .ToListAsync(cancellationToken);
    }
}
