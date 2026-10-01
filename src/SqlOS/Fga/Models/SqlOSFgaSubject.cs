using SqlOS.AuthServer.Contracts;
using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.Fga.Models;

/// <summary>
/// Core security subject (can be user, group, or service account).
/// </summary>
/// <remarks>
/// The root of the subject aggregate. Its kind is fixed when it is created, with the typed record
/// that carries the kind's lifecycle: a user and a group can be deactivated, a service account
/// expires, an agent is always active. A group owns its memberships; a group cannot be a member
/// of another group.
/// </remarks>
public sealed class SqlOSFgaSubject : ISqlOSAggregate
{
    internal const string UserType = "user";
    internal const string GroupType = "group";
    internal const string ServiceAccountType = "service_account";
    internal const string AgentType = "agent";

    private readonly DomainEventBuffer _events = new();

    private SqlOSFgaSubject()
    {
    }

    public string Id { get; private set; } = string.Empty;
    public string SubjectTypeId { get; private set; } = string.Empty;
    public string? OrganizationId { get; private set; }
    public string? ExternalRef { get; private set; }
    public string DisplayName { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    // Navigation
    public SqlOSFgaSubjectType? SubjectType { get; private set; }
    public SqlOSFgaUser? User { get; private set; }
    public SqlOSFgaAgent? Agent { get; private set; }
    public SqlOSFgaUserGroup? UserGroup { get; private set; }
    public SqlOSFgaServiceAccount? ServiceAccount { get; private set; }
    public ICollection<SqlOSFgaGrant> Grants { get; private set; } = new List<SqlOSFgaGrant>();

    DomainEventBuffer ISqlOSAggregate.Events => _events;

    /// <summary>A subject of <paramref name="subjectTypeId"/> without a typed record (an application-defined type).</summary>
    internal static SqlOSFgaSubject Create(
        string id,
        string subjectTypeId,
        string displayName,
        string? organizationId,
        string? externalRef,
        FgaActor actor,
        DateTime now)
    {
        var subject = new SqlOSFgaSubject
        {
            Id = id,
            SubjectTypeId = subjectTypeId,
            DisplayName = displayName,
            OrganizationId = organizationId,
            ExternalRef = externalRef,
            CreatedAt = now,
            UpdatedAt = now
        };
        subject._events.Raise(new FgaSubjectCreated(id, subjectTypeId, organizationId, actor));
        return subject;
    }

    internal static SqlOSFgaSubject CreateUser(
        string id,
        string displayName,
        string? organizationId,
        string? externalRef,
        string userId,
        string? email,
        bool isActive,
        FgaActor actor,
        DateTime now)
    {
        var subject = Create(id, UserType, displayName, organizationId, externalRef, actor, now);
        subject.User = new SqlOSFgaUser(userId, id, email, isActive, now);
        return subject;
    }

    internal static SqlOSFgaSubject CreateAgent(
        string id,
        string displayName,
        string? organizationId,
        string? externalRef,
        string agentId,
        string? agentType,
        string? description,
        FgaActor actor,
        DateTime now)
    {
        var subject = Create(id, AgentType, displayName, organizationId, externalRef, actor, now);
        subject.Agent = new SqlOSFgaAgent(agentId, id, agentType, description, now);
        return subject;
    }

    internal static SqlOSFgaSubject CreateServiceAccount(
        string id,
        string displayName,
        string? organizationId,
        string? externalRef,
        string accountId,
        string clientId,
        string clientSecretHash,
        string? description,
        DateTime? expiresAt,
        string configurationOwner,
        string? configurationSourceKey,
        FgaActor actor,
        DateTime now)
    {
        var subject = Create(id, ServiceAccountType, displayName, organizationId, externalRef, actor, now);
        subject.ServiceAccount = new SqlOSFgaServiceAccount(
            accountId,
            id,
            clientId,
            clientSecretHash,
            description,
            expiresAt,
            configurationOwner,
            configurationSourceKey,
            now);
        return subject;
    }

    internal static SqlOSFgaSubject CreateGroup(
        string id,
        string displayName,
        string? organizationId,
        string? externalRef,
        string groupId,
        string? description,
        string? groupType,
        FgaActor actor,
        DateTime now)
    {
        var subject = Create(id, GroupType, displayName, organizationId, externalRef, actor, now);
        subject.UserGroup = new SqlOSFgaUserGroup(groupId, id, displayName, description, groupType, now);
        return subject;
    }

    /// <summary>
    /// Gives a subject created without its typed record (a bare built-in subject, which every check
    /// denies) that record. The caller has loaded the record if it exists.
    /// </summary>
    internal void AttachUser(string userId, string? email, bool isActive, DateTime now)
        => User = Attach(User, UserType, () => new SqlOSFgaUser(userId, Id, email, isActive, now));

    internal void AttachAgent(string agentId, string? agentType, string? description, DateTime now)
        => Agent = Attach(Agent, AgentType, () => new SqlOSFgaAgent(agentId, Id, agentType, description, now));

    internal void AttachServiceAccount(string accountId, string clientId, string clientSecretHash, string? description, DateTime? expiresAt, DateTime now)
        => ServiceAccount = Attach(ServiceAccount, ServiceAccountType, () => new SqlOSFgaServiceAccount(
            accountId, Id, clientId, clientSecretHash, description, expiresAt, SqlOSConfigurationOwners.Dashboard, null, now));

    internal void AttachGroup(string groupId, string? description, string? groupType, DateTime now)
        => UserGroup = Attach(UserGroup, GroupType, () => new SqlOSFgaUserGroup(groupId, Id, DisplayName, description, groupType, now));

    /// <summary>Sets the display name, organization and external reference exactly as given.</summary>
    internal void Describe(string displayName, string? organizationId, string? externalRef, DateTime now)
    {
        UpdatedAt = now;
        if (DisplayName == displayName && OrganizationId == organizationId && ExternalRef == externalRef)
        {
            return;
        }

        (DisplayName, OrganizationId, ExternalRef) = (displayName, organizationId, externalRef);
        _events.Raise(new FgaSubjectDescribed(Id));
    }

    /// <summary>Deactivates or reactivates a user or a group: an inactive one is denied every check.</summary>
    internal void ChangeActivity(bool isActive, FgaActor actor, DateTime now)
    {
        var changed = SubjectTypeId switch
        {
            UserType => Typed(User).ChangeActivity(isActive, now),
            GroupType => Typed(UserGroup).ChangeActivity(isActive, now),
            _ => throw new InvalidOperationException($"FGA subject '{Id}' of type '{SubjectTypeId}' has no active state.")
        };
        if (changed)
        {
            _events.Raise(new FgaSubjectActivationChanged(Id, isActive, actor));
        }
    }

    internal void DescribeUser(string? email, DateTime now) => Typed(User).Describe(email, now);

    internal void DescribeAgent(string? agentType, string? description, DateTime now)
        => Typed(Agent).Describe(agentType, description, now);

    internal void DescribeGroup(string name, string? description, string? groupType, DateTime now)
        => Typed(UserGroup).Describe(name, description, groupType, now);

    internal void DescribeServiceAccount(string? description, DateTime now)
        => Typed(ServiceAccount).Describe(description, now);

    internal void ChangeServiceAccountCredential(string clientId, string clientSecretHash, DateTime now)
        => Typed(ServiceAccount).ChangeCredential(clientId, clientSecretHash, now);

    /// <summary>Changes when the service account stops authorizing; expiring it now revokes it.</summary>
    internal void ChangeServiceAccountExpiry(DateTime? expiresAt, FgaActor actor, DateTime now)
    {
        if (Typed(ServiceAccount).ChangeExpiry(expiresAt, now))
        {
            _events.Raise(new FgaServiceAccountExpiryChanged(Id, expiresAt, actor));
        }
    }

    /// <summary>Stamps the last activity of the typed record: a user's sign-in, an agent's run, a service account's use.</summary>
    internal void RecordActivity(DateTime now)
    {
        switch (SubjectTypeId)
        {
            case UserType:
                Typed(User).RecordLogin(now);
                break;
            case AgentType:
                Typed(Agent).RecordRun(now);
                break;
            case ServiceAccountType:
                Typed(ServiceAccount).RecordUse(now);
                break;
            default:
                throw new InvalidOperationException($"FGA subject '{Id}' of type '{SubjectTypeId}' records no activity.");
        }
    }

    internal void TouchServiceAccount(DateTime now) => Typed(ServiceAccount).Touch(now);

    internal void ReconcileServiceAccount(string fingerprint, DateTime now) => Typed(ServiceAccount).Reconciled(fingerprint, now);

    internal void OrphanServiceAccount(DateTime now) => Typed(ServiceAccount).Orphaned(now);

    /// <summary>
    /// Adds <paramref name="member"/> to this group. The caller adds the returned row once it knows
    /// the subject is not already a member.
    /// </summary>
    internal SqlOSFgaUserGroupMembership AddMember(SqlOSFgaSubject member, FgaActor actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(member);
        var group = Typed(UserGroup);
        if (member.SubjectTypeId == GroupType)
        {
            throw new InvalidOperationException("Groups cannot be members of other groups");
        }

        _events.Raise(new FgaGroupMemberAdded(Id, group.Id, member.Id, actor));
        return new SqlOSFgaUserGroupMembership(member.Id, group.Id, now);
    }

    /// <summary>Records that <paramref name="membership"/> leaves this group; the caller deletes its row.</summary>
    internal void RemoveMember(SqlOSFgaUserGroupMembership membership, FgaActor actor)
    {
        ArgumentNullException.ThrowIfNull(membership);
        var group = Typed(UserGroup);
        if (membership.UserGroupId != group.Id)
        {
            throw new InvalidOperationException($"Subject '{membership.SubjectId}' is not a member of group '{group.Id}'.");
        }

        _events.Raise(new FgaGroupMemberRemoved(Id, group.Id, membership.SubjectId, actor));
    }

    /// <summary>
    /// Records that this user subject absorbed <paramref name="merged"/>, another user subject of
    /// the same person, whose memberships and grants the caller moved or dropped; the caller deletes
    /// it with its typed record.
    /// </summary>
    internal void Absorb(
        SqlOSFgaSubject merged,
        IReadOnlyList<string> groupIds,
        IReadOnlyList<string> movedGrantIds,
        IReadOnlyList<string> droppedGrantIds,
        FgaActor actor)
    {
        ArgumentNullException.ThrowIfNull(merged);
        if (SubjectTypeId != UserType || merged.SubjectTypeId != UserType || merged.Id == Id)
        {
            throw new InvalidOperationException($"FGA subject '{merged.Id}' cannot merge into '{Id}': only another user subject merges into a user subject.");
        }

        _events.Raise(new FgaSubjectMerged(Id, merged.Id, groupIds, movedGrantIds, droppedGrantIds, actor));
    }

    /// <summary>Records that the subject is deleted; the caller deletes it with its typed record.</summary>
    internal void Delete(FgaActor actor) => _events.Raise(new FgaSubjectDeleted(Id, SubjectTypeId, actor));

    private T Attach<T>(T? current, string kind, Func<T> create)
        where T : class
        => SubjectTypeId != kind
            ? throw new InvalidOperationException($"FGA subject '{Id}' is a '{SubjectTypeId}', not a '{kind}'.")
            : current ?? create();

    private T Typed<T>(T? record)
        where T : class
        => record ?? throw new InvalidOperationException(
            $"FGA subject '{Id}' of type '{SubjectTypeId}' has no {typeof(T).Name} record loaded.");
}
