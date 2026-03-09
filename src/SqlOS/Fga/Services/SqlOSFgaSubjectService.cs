using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Services;

public class SqlOSFgaSubjectService : ISqlOSFgaSubjectService
{
    private readonly ISqlOSFgaDbContext _context;
    private readonly ILogger<SqlOSFgaSubjectService> _logger;

    public SqlOSFgaSubjectService(
        ISqlOSFgaDbContext context,
        ILogger<SqlOSFgaSubjectService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<SqlOSFgaSubject> CreateSubjectAsync(
        string displayName,
        string subjectTypeId,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default)
    {
        var subject = new SqlOSFgaSubject
        {
            Id = $"subj_{Guid.NewGuid():N}"[..30],
            SubjectTypeId = subjectTypeId,
            DisplayName = displayName,
            OrganizationId = organizationId,
            ExternalRef = externalRef,
        };

        _context.Set<SqlOSFgaSubject>().Add(subject);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created subject {SubjectId} ({DisplayName}) of type {Type}",
            subject.Id, displayName, subjectTypeId);

        return subject;
    }

    public async Task<SqlOSFgaUserGroup> CreateGroupAsync(
        string name,
        string? description = null,
        string? groupType = null,
        CancellationToken cancellationToken = default)
    {
        var subject = await CreateSubjectAsync(name, "group", cancellationToken: cancellationToken);

        var group = new SqlOSFgaUserGroup
        {
            Id = $"grp_{Guid.NewGuid():N}"[..30],
            Name = name,
            Description = description,
            GroupType = groupType,
            SubjectId = subject.Id,
        };

        _context.Set<SqlOSFgaUserGroup>().Add(group);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created group {GroupId} ({Name})", group.Id, name);

        return group;
    }

    public async Task<SqlOSFgaUser> CreateUserAsync(
        string displayName,
        string? email = null,
        bool isActive = true,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default)
    {
        var subject = await CreateSubjectAsync(displayName, "user", organizationId, externalRef, cancellationToken);

        var user = new SqlOSFgaUser
        {
            Id = $"usr_{Guid.NewGuid():N}"[..30],
            SubjectId = subject.Id,
            Email = email,
            IsActive = isActive,
        };

        _context.Set<SqlOSFgaUser>().Add(user);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created user {UserId} ({DisplayName})", user.Id, displayName);

        return user;
    }

    public async Task<SqlOSFgaAgent> CreateAgentAsync(
        string displayName,
        string? agentType = null,
        string? description = null,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default)
    {
        var subject = await CreateSubjectAsync(displayName, "agent", organizationId, externalRef, cancellationToken);

        var agent = new SqlOSFgaAgent
        {
            Id = $"agt_{Guid.NewGuid():N}"[..30],
            SubjectId = subject.Id,
            AgentType = agentType,
            Description = description,
        };

        _context.Set<SqlOSFgaAgent>().Add(agent);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created agent {AgentId} ({DisplayName})", agent.Id, displayName);

        return agent;
    }

    public async Task<SqlOSFgaServiceAccount> CreateServiceAccountAsync(
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

        var serviceAccount = new SqlOSFgaServiceAccount
        {
            Id = $"sa_{Guid.NewGuid():N}"[..30],
            SubjectId = subject.Id,
            ClientId = clientId,
            ClientSecretHash = clientSecretHash,
            Description = description,
            ExpiresAt = expiresAt,
        };

        _context.Set<SqlOSFgaServiceAccount>().Add(serviceAccount);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created service account {ServiceAccountId} ({DisplayName})", serviceAccount.Id, displayName);

        return serviceAccount;
    }

    public async Task AddToGroupAsync(string subjectId, string userGroupId, CancellationToken cancellationToken = default)
    {
        // Validate subject is NOT a group type (no nested groups allowed)
        var subject = await _context.Set<SqlOSFgaSubject>()
            .FirstOrDefaultAsync(s => s.Id == subjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Subject '{subjectId}' not found");

        if (subject.SubjectTypeId == "group")
        {
            throw new InvalidOperationException("Groups cannot be members of other groups");
        }

        var exists = await _context.Set<SqlOSFgaUserGroupMembership>()
            .AnyAsync(m => m.SubjectId == subjectId && m.UserGroupId == userGroupId, cancellationToken);

        if (exists) return;

        var membership = new SqlOSFgaUserGroupMembership
        {
            SubjectId = subjectId,
            UserGroupId = userGroupId,
        };

        _context.Set<SqlOSFgaUserGroupMembership>().Add(membership);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Added subject {SubjectId} to group {GroupId}", subjectId, userGroupId);
    }

    public async Task RemoveFromGroupAsync(string subjectId, string userGroupId, CancellationToken cancellationToken = default)
    {
        var membership = await _context.Set<SqlOSFgaUserGroupMembership>()
            .FirstOrDefaultAsync(m => m.SubjectId == subjectId && m.UserGroupId == userGroupId, cancellationToken);

        if (membership == null) return;

        _context.Set<SqlOSFgaUserGroupMembership>().Remove(membership);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Removed subject {SubjectId} from group {GroupId}", subjectId, userGroupId);
    }

    public async Task<List<string>> ResolveSubjectIdsAsync(string subjectId, CancellationToken cancellationToken = default)
    {
        var subjects = new List<string> { subjectId };

        var groupSubjectIds = await _context.Set<SqlOSFgaUserGroupMembership>()
            .Where(m => m.SubjectId == subjectId)
            .Join(_context.Set<SqlOSFgaUserGroup>(),
                m => m.UserGroupId,
                g => g.Id,
                (m, g) => g.SubjectId)
            .ToListAsync(cancellationToken);

        subjects.AddRange(groupSubjectIds);
        return subjects;
    }

    public async Task<List<SqlOSFgaUserGroup>> GetGroupsForSubjectAsync(string subjectId, CancellationToken cancellationToken = default)
    {
        return await _context.Set<SqlOSFgaUserGroupMembership>()
            .Where(m => m.SubjectId == subjectId)
            .Join(_context.Set<SqlOSFgaUserGroup>(),
                m => m.UserGroupId,
                g => g.Id,
                (m, g) => g)
            .ToListAsync(cancellationToken);
    }
}
