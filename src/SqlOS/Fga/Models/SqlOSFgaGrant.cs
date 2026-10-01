using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.Fga.Models;

/// <summary>
/// A grant assigns a role to a subject for a specific resource.
/// </summary>
/// <remarks>
/// A grant is created only with a <see cref="GrantAuthority"/>, the proof that its caller may write
/// it, and is unique per subject, role, resource and window: <c>SqlOSFgaGrants</c> finds an
/// equivalent grant before a writer creates one.
/// </remarks>
public sealed class SqlOSFgaGrant : ISqlOSAggregate
{
    private readonly DomainEventBuffer _events = new();

    private SqlOSFgaGrant()
    {
    }

    public string Id { get; private set; } = string.Empty;
    public string SubjectId { get; private set; } = string.Empty;
    public string ResourceId { get; private set; } = string.Empty;
    public string RoleId { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public DateTime? EffectiveFrom { get; private set; }
    public DateTime? EffectiveTo { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    // Navigation
    public SqlOSFgaSubject? Subject { get; private set; }
    public SqlOSFgaResource? Resource { get; private set; }
    public SqlOSFgaRole? Role { get; private set; }

    DomainEventBuffer ISqlOSAggregate.Events => _events;

    /// <summary>When the grant is in effect: both bounds included, as the access check reads them.</summary>
    internal TimeWindow Window => TimeWindow.Between(EffectiveFrom, EffectiveTo);

    /// <summary>
    /// Grants <paramref name="role"/> to <paramref name="subject"/> on the resource during
    /// <paramref name="window"/>. A tenant-controlled <paramref name="authority"/> must cover exactly
    /// this subject and resource.
    /// </summary>
    internal static SqlOSFgaGrant Create(
        string id,
        SqlOSFgaSubject subject,
        SqlOSFgaRole role,
        string resourceId,
        TimeWindow window,
        string? description,
        GrantAuthority authority,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(authority);
        authority.EnsureCovers(subject.Id, resourceId);
        var grant = new SqlOSFgaGrant
        {
            Id = id,
            SubjectId = subject.Id,
            RoleId = role.Id,
            ResourceId = resourceId,
            EffectiveFrom = window.EffectiveFrom,
            EffectiveTo = window.EffectiveTo,
            Description = description,
            CreatedAt = now,
            UpdatedAt = now
        };
        grant._events.Raise(new FgaGrantCreated(
            id,
            subject.Id,
            role.Id,
            resourceId,
            window.EffectiveFrom,
            window.EffectiveTo,
            subject.OrganizationId,
            authority.Grantor));
        return grant;
    }

    /// <summary>Whether this grant gives the same role to the same subject on the same resource during the same window.</summary>
    internal bool IsEquivalentTo(string subjectId, string roleId, string resourceId, TimeWindow window)
        => SubjectId == subjectId && RoleId == roleId && ResourceId == resourceId
            && EffectiveFrom == window.EffectiveFrom && EffectiveTo == window.EffectiveTo;

    internal void Describe(string? description, DateTime now)
    {
        UpdatedAt = now;
        if (Description == description)
        {
            return;
        }

        Description = description;
        _events.Raise(new FgaGrantDescribed(Id));
    }

    /// <summary>
    /// Gives the grant to <paramref name="subject"/>, which absorbed the subject that held it; the
    /// merge records the move.
    /// </summary>
    internal void MoveTo(SqlOSFgaSubject subject, GrantAuthority authority, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(authority);
        authority.EnsureCovers(subject.Id, ResourceId);
        (SubjectId, UpdatedAt) = (subject.Id, now);
    }

    /// <summary>
    /// Records that the grant is revoked; the caller deletes its row in the same save.
    /// <paramref name="reason"/> names the change that caused it, when another one did.
    /// </summary>
    internal void Revoke(FgaActor actor, string? reason = null)
        => _events.Raise(new FgaGrantRevoked(Id, SubjectId, RoleId, ResourceId, reason, actor));
}
