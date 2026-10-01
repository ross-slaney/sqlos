using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SqlOS.Domain;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using static SqlOS.Fga.SqlOSFgaWrites;

namespace SqlOS.Extensions;

public static partial class SqlOSErgonomicsExtensions
{
    /// <summary>
    /// Idempotently provisions an FGA subject of type <c>user</c> and its typed user record.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="subjectId">The stable identifier used for authorization checks and grants.</param>
    /// <param name="displayName">The subject's display name.</param>
    /// <param name="email">An optional email address. When omitted for an existing user, the current value is preserved.</param>
    /// <param name="organizationId">An optional organization identifier. When omitted for an existing subject, the current value is preserved.</param>
    /// <param name="externalRef">An optional external identifier. New subjects default it to <paramref name="subjectId"/>.</param>
    /// <param name="isActive">An optional active state. When omitted for an existing user, the current value is preserved.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>The added or updated tracked user record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required value is empty, or <paramref name="subjectId"/> already belongs to a different subject type.
    /// </exception>
    /// <remarks>This method tracks changes but does not save them.</remarks>
    public static async Task<SqlOSFgaUser> ProvisionUserSubjectAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        string displayName,
        string? email = null,
        string? organizationId = null,
        string? externalRef = null,
        bool? isActive = null,
        CancellationToken cancellationToken = default)
    {
        var (subject, now) = await ProvisionSubjectAsync(context, subjectId, SqlOSFgaSubject.UserType, displayName, organizationId, externalRef, cancellationToken);
        if (subject.User == null)
        {
            subject.AttachUser(TypedRecordId("usr", subject.Id), NormalizeOptional(email), isActive ?? true, now);
            return subject.User!;
        }

        if (email != null)
        {
            subject.DescribeUser(NormalizeOptional(email), now);
        }

        if (isActive.HasValue)
        {
            subject.ChangeActivity(isActive.Value, FgaActor.Host, now);
        }

        return subject.User;
    }

    /// <summary>
    /// Idempotently provisions an FGA subject of type <c>agent</c> and its typed agent record.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="subjectId">The stable identifier used for authorization checks and grants.</param>
    /// <param name="displayName">The subject's display name.</param>
    /// <param name="agentType">An optional application-defined agent type. When omitted for an existing agent, the current value is preserved.</param>
    /// <param name="description">An optional description. When omitted for an existing agent, the current value is preserved.</param>
    /// <param name="organizationId">An optional organization identifier. When omitted for an existing subject, the current value is preserved.</param>
    /// <param name="externalRef">An optional external identifier. New subjects default it to <paramref name="subjectId"/>.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>The added or updated tracked agent record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required value is empty, or <paramref name="subjectId"/> already belongs to a different subject type.
    /// </exception>
    /// <remarks>This method tracks changes but does not save them.</remarks>
    public static async Task<SqlOSFgaAgent> ProvisionAgentSubjectAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        string displayName,
        string? agentType = null,
        string? description = null,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default)
    {
        var (subject, now) = await ProvisionSubjectAsync(context, subjectId, SqlOSFgaSubject.AgentType, displayName, organizationId, externalRef, cancellationToken);
        if (subject.Agent == null)
        {
            subject.AttachAgent(TypedRecordId("agt", subject.Id), NormalizeOptional(agentType), NormalizeOptional(description), now);
            return subject.Agent!;
        }

        subject.DescribeAgent(
            agentType != null ? NormalizeOptional(agentType) : subject.Agent.AgentType,
            description != null ? NormalizeOptional(description) : subject.Agent.Description,
            now);
        return subject.Agent;
    }

    /// <summary>
    /// Idempotently provisions an FGA subject of type <c>service_account</c> and its typed service-account record.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="subjectId">The stable identifier used for authorization checks and grants.</param>
    /// <param name="displayName">The subject's display name.</param>
    /// <param name="clientId">The service account's client identifier.</param>
    /// <param name="clientSecretHash">The application-provided hash of the service account secret.</param>
    /// <param name="description">An optional description. When omitted for an existing account, the current value is preserved.</param>
    /// <param name="expiresAt">An optional expiration time. When omitted for an existing account, the current value is preserved.</param>
    /// <param name="organizationId">An optional organization identifier. When omitted for an existing subject, the current value is preserved.</param>
    /// <param name="externalRef">An optional external identifier. New subjects default it to <paramref name="subjectId"/>.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>The added or updated tracked service-account record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required value is empty, or <paramref name="subjectId"/> already belongs to a different subject type.
    /// </exception>
    /// <remarks>This method tracks changes but does not save them.</remarks>
    public static async Task<SqlOSFgaServiceAccount> ProvisionServiceAccountSubjectAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        string displayName,
        string clientId,
        string clientSecretHash,
        string? description = null,
        DateTime? expiresAt = null,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default)
    {
        var (subject, now) = await ProvisionSubjectAsync(context, subjectId, SqlOSFgaSubject.ServiceAccountType, displayName, organizationId, externalRef, cancellationToken);
        if (subject.ServiceAccount == null)
        {
            subject.AttachServiceAccount(
                TypedRecordId("sa", subject.Id),
                RequireValue(clientId, nameof(clientId)),
                RequireValue(clientSecretHash, nameof(clientSecretHash)),
                NormalizeOptional(description),
                expiresAt,
                now);
            return subject.ServiceAccount!;
        }

        subject.ChangeServiceAccountCredential(RequireValue(clientId, nameof(clientId)), RequireValue(clientSecretHash, nameof(clientSecretHash)), now);
        if (description != null)
        {
            subject.DescribeServiceAccount(NormalizeOptional(description), now);
        }

        if (expiresAt.HasValue)
        {
            subject.ChangeServiceAccountExpiry(expiresAt, FgaActor.Host, now);
        }

        return subject.ServiceAccount;
    }

    /// <summary>
    /// Idempotently provisions an FGA subject of type <c>group</c> and its group record, keyed by a
    /// stable subject identifier, optionally owned by an organization.
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="subjectId">The stable identifier of the group as an authorization principal, used for grants and checks.</param>
    /// <param name="name">The group's name, which is also the subject's display name.</param>
    /// <param name="description">An optional description. When omitted for an existing group, the current value is preserved.</param>
    /// <param name="groupType">An optional application-defined group type. When omitted for an existing group, the current value is preserved.</param>
    /// <param name="organizationId">An optional owning organization. When omitted for an existing subject, the current value is preserved.</param>
    /// <param name="externalRef">An optional external identifier. New subjects default it to <paramref name="subjectId"/>.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>
    /// The added or updated tracked group record. Its <see cref="SqlOSFgaUserGroup.Id"/> is the
    /// membership container that <see cref="ISqlOSFgaSubjectService.AddToGroupAsync"/> takes.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A required value is empty, or <paramref name="subjectId"/> already belongs to a different subject type.
    /// </exception>
    /// <remarks>
    /// This method tracks changes but does not save them. <c>OrganizationId</c> is metadata a trusted
    /// workflow can validate; checks and group resolution do not enforce it.
    /// </remarks>
    public static async Task<SqlOSFgaUserGroup> ProvisionGroupSubjectAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        string name,
        string? description = null,
        string? groupType = null,
        string? organizationId = null,
        string? externalRef = null,
        CancellationToken cancellationToken = default)
    {
        var (subject, now) = await ProvisionSubjectAsync(context, subjectId, SqlOSFgaSubject.GroupType, name, organizationId, externalRef, cancellationToken);
        if (subject.UserGroup == null)
        {
            subject.AttachGroup(TypedRecordId("grp", subject.Id), NormalizeOptional(description), NormalizeOptional(groupType), now);
            return subject.UserGroup!;
        }

        subject.DescribeGroup(
            subject.DisplayName,
            description != null ? NormalizeOptional(description) : subject.UserGroup.Description,
            groupType != null ? NormalizeOptional(groupType) : subject.UserGroup.GroupType,
            now);
        return subject.UserGroup;
    }

    /// <summary>
    /// Records that a subject was just active: a user signed in (<see cref="SqlOSFgaUser.LastLoginAt"/>),
    /// an agent ran (<see cref="SqlOSFgaAgent.LastRunAt"/>) or a service account was used
    /// (<see cref="SqlOSFgaServiceAccount.LastUsedAt"/>).
    /// </summary>
    /// <param name="context">The application FGA context.</param>
    /// <param name="subjectId">The identifier of a provisioned user, agent, or service-account subject.</param>
    /// <param name="cancellationToken">A token that can cancel database lookups.</param>
    /// <returns>A task that completes when the activity has been recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="subjectId"/> is empty, or the subject does not exist or is not a user, agent, or service account.
    /// </exception>
    /// <remarks>This method tracks changes but does not save them.</remarks>
    public static async Task RecordSubjectActivityAsync(
        this ISqlOSFgaDbContext context,
        string subjectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var normalizedSubjectId = RequireValue(subjectId, nameof(subjectId));
        var subject = await FindSubjectAsync(context, normalizedSubjectId, cancellationToken)
            ?? throw new InvalidOperationException($"FGA subject '{normalizedSubjectId}' was not found.");
        await LoadTypedRecordAsync(context, subject, cancellationToken);
        subject.RecordActivity(Now(context));
    }

    /// <summary>
    /// The subject <paramref name="subjectId"/> of <paramref name="subjectTypeId"/>, created or
    /// described, with its typed record loaded when it has one.
    /// </summary>
    private static async Task<(SqlOSFgaSubject Subject, DateTime Now)> ProvisionSubjectAsync(
        ISqlOSFgaDbContext context,
        string subjectId,
        string subjectTypeId,
        string displayName,
        string? organizationId,
        string? externalRef,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var normalizedSubjectId = RequireValue(subjectId, nameof(subjectId));
        var now = Now(context);
        var subject = await FindSubjectAsync(context, normalizedSubjectId, cancellationToken);
        if (subject == null)
        {
            subject = SqlOSFgaSubject.Create(
                normalizedSubjectId,
                subjectTypeId,
                RequireValue(displayName, nameof(displayName)),
                NormalizeOptional(organizationId),
                NormalizeOptional(externalRef) ?? normalizedSubjectId,
                FgaActor.Host,
                now);
            context.Set<SqlOSFgaSubject>().Add(subject);
            return (subject, now);
        }

        if (!string.Equals(subject.SubjectTypeId, subjectTypeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"FGA subject '{normalizedSubjectId}' already exists as type '{subject.SubjectTypeId}', not '{subjectTypeId}'.");
        }

        subject.Describe(
            RequireValue(displayName, nameof(displayName)),
            organizationId != null ? NormalizeOptional(organizationId) : subject.OrganizationId,
            externalRef != null ? NormalizeOptional(externalRef) : subject.ExternalRef,
            now);
        await LoadTypedRecordAsync(context, subject, cancellationToken);
        return (subject, now);
    }

    private static Task LoadTypedRecordAsync(ISqlOSFgaDbContext context, SqlOSFgaSubject subject, CancellationToken cancellationToken)
        => subject.SubjectTypeId switch
        {
            SqlOSFgaSubject.UserType => context.Set<SqlOSFgaUser>().Where(row => row.SubjectId == subject.Id).LoadAsync(cancellationToken),
            SqlOSFgaSubject.AgentType => context.Set<SqlOSFgaAgent>().Where(row => row.SubjectId == subject.Id).LoadAsync(cancellationToken),
            SqlOSFgaSubject.ServiceAccountType => context.Set<SqlOSFgaServiceAccount>().Where(row => row.SubjectId == subject.Id).LoadAsync(cancellationToken),
            SqlOSFgaSubject.GroupType => context.Set<SqlOSFgaUserGroup>().Where(row => row.SubjectId == subject.Id).LoadAsync(cancellationToken),
            _ => Task.CompletedTask
        };

    private static string TypedRecordId(string prefix, string subjectId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{prefix}\n{subjectId}"));
        return $"{prefix}::{Convert.ToHexString(bytes).ToLowerInvariant()[..32]}";
    }
}
