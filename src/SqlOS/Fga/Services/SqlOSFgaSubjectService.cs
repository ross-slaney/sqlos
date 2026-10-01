using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SqlOS.AuthServer.Contracts;
using SqlOS.Domain;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Processes;

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
        var subject = await CreateAsync(now => SqlOSFgaSubject.Create(
            NewSubjectId(), subjectTypeId, displayName, organizationId, externalRef, FgaActor.Host, now), cancellationToken);
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
        var subject = await CreateAsync(now => SqlOSFgaSubject.CreateGroup(
            NewSubjectId(), name, null, null, NewId("grp"), description, groupType, FgaActor.Host, now), cancellationToken);
        _logger.LogInformation("Created group {GroupId} ({Name})", subject.UserGroup!.Id, name);
        return subject.UserGroup;
    }

    public async Task<SqlOSFgaUser> CreateUserAsync(
        string displayName,
        string? email = null,
        bool isActive = true,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default)
    {
        var subject = await CreateAsync(now => SqlOSFgaSubject.CreateUser(
            NewSubjectId(), displayName, organizationId, externalRef, NewId("usr"), email, isActive, FgaActor.Host, now), cancellationToken);
        _logger.LogInformation("Created user {UserId} ({DisplayName})", subject.User!.Id, displayName);
        return subject.User;
    }

    public async Task<SqlOSFgaAgent> CreateAgentAsync(
        string displayName,
        string? agentType = null,
        string? description = null,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default)
    {
        var subject = await CreateAsync(now => SqlOSFgaSubject.CreateAgent(
            NewSubjectId(), displayName, organizationId, externalRef, NewId("agt"), agentType, description, FgaActor.Host, now), cancellationToken);
        _logger.LogInformation("Created agent {AgentId} ({DisplayName})", subject.Agent!.Id, displayName);
        return subject.Agent;
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
        var subject = await CreateAsync(now => SqlOSFgaSubject.CreateServiceAccount(
            NewSubjectId(),
            displayName,
            organizationId,
            externalRef,
            NewId("sa"),
            clientId,
            clientSecretHash,
            description,
            expiresAt,
            SqlOSConfigurationOwners.Dashboard,
            configurationSourceKey: null,
            FgaActor.Host,
            now), cancellationToken);
        _logger.LogInformation("Created service account {ServiceAccountId} ({DisplayName})", subject.ServiceAccount!.Id, displayName);
        return subject.ServiceAccount;
    }

    public async Task AddToGroupAsync(string subjectId, string userGroupId, CancellationToken cancellationToken = default)
    {
        if (await new ChangeFgaGroupMembership(_context).AddAsync(subjectId, userGroupId, FgaActor.Host, cancellationToken))
        {
            _logger.LogInformation("Added subject {SubjectId} to group {GroupId}", subjectId, userGroupId);
        }
    }

    public async Task RemoveFromGroupAsync(string subjectId, string userGroupId, CancellationToken cancellationToken = default)
    {
        if (await new ChangeFgaGroupMembership(_context).RemoveAsync(subjectId, userGroupId, FgaActor.Host, cancellationToken))
        {
            _logger.LogInformation("Removed subject {SubjectId} from group {GroupId}", subjectId, userGroupId);
        }
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

    private Task<SqlOSFgaSubject> CreateAsync(Func<DateTime, SqlOSFgaSubject> create, CancellationToken cancellationToken)
        => new CreateFgaSubject(_context).ExecuteAsync(create, cancellationToken);

    // The prefix, an underscore, then the start of a GUID, 30 characters in all.
    private static string NewSubjectId() => NewId("subj");

    private static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..30];
}
