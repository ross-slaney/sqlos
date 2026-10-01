namespace SqlOS.Domain;

/// <summary>
/// This caller may write this FGA grant (<c>docs/architecture/domain-model.md</c> §3.5): the only
/// way <c>SqlOSFgaGrant.Create</c> creates a grant.
/// </summary>
/// <remarks>
/// The operator (the FGA dashboard and the admin API), host code (the documented FGA API and code
/// seeds) and SqlOS's own subject merge hold an unrestricted authority. A tenant-controlled
/// authority, a SCIM mapping, is produced only after the resource was proven inside the
/// connection's grant boundary (#421), and covers exactly that subject on that resource, so it
/// cannot grant anywhere else. Producers are listed in <c>proof-producers.txt</c>.
/// </remarks>
internal sealed class GrantAuthority : ISqlOSProof
{
    internal GrantAuthority(FgaActor grantor)
    {
        Grantor = grantor;
    }

    internal GrantAuthority(FgaActor grantor, string subjectId, string resourceId, string boundaryResourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundaryResourceId);
        Grantor = grantor;
        SubjectId = subjectId;
        ResourceId = resourceId;
        BoundaryResourceId = boundaryResourceId;
    }

    /// <summary>Who grants, as the audit records it.</summary>
    public FgaActor Grantor { get; }

    /// <summary>The only subject a tenant-controlled authority covers, or null when unrestricted.</summary>
    public string? SubjectId { get; }

    /// <summary>The only resource a tenant-controlled authority covers, or null when unrestricted.</summary>
    public string? ResourceId { get; }

    /// <summary>The tenant boundary <see cref="ResourceId"/> was proven inside, or null when unrestricted.</summary>
    public string? BoundaryResourceId { get; }

    public bool IsTenantControlled => BoundaryResourceId is not null;

    /// <summary>Refuses a grant this authority was not produced for.</summary>
    internal void EnsureCovers(string subjectId, string resourceId)
    {
        if (IsTenantControlled
            && (!string.Equals(subjectId, SubjectId, StringComparison.Ordinal)
                || !string.Equals(resourceId, ResourceId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"The grant authority covers subject '{SubjectId}' on resource '{ResourceId}' inside boundary '{BoundaryResourceId}', not subject '{subjectId}' on resource '{resourceId}'.");
        }
    }

    public override string ToString() => $"{nameof(GrantAuthority)}({Grantor})";
}

/// <summary>Who changed the FGA model, as an audit row's actor records it.</summary>
internal readonly record struct FgaActor(string Type, string? Id)
{
    /// <summary>An operator, through the FGA dashboard or the admin API.</summary>
    public static FgaActor Operator { get; } = new("admin", null);

    /// <summary>The host application, through the documented FGA API.</summary>
    public static FgaActor Host { get; } = new("application", null);

    /// <summary>SqlOS reconciling code-owned configuration at startup.</summary>
    public static FgaActor Startup { get; } = new("system", "startup");

    /// <summary>SqlOS migrating stored data to the current model at startup.</summary>
    public static FgaActor Upgrade { get; } = new("system", "upgrade");

    /// <summary>A SCIM directory, through the connection with <paramref name="connectionId"/>.</summary>
    public static FgaActor Directory(string connectionId) => new("scim", connectionId);

    public override string ToString() => Id is null ? Type : $"{Type}:{Id}";
}
