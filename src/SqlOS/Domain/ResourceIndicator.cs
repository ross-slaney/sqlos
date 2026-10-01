namespace SqlOS.Domain;

/// <summary>
/// An RFC 8707 resource indicator: the API a token is requested for, which becomes its
/// <c>aud</c>.
/// </summary>
/// <remarks>
/// <para>
/// These are the rules SqlOS 7.x applies: the request value is trimmed, a blank value means no
/// resource was requested, and a resource matches an audience, a stored binding or a later
/// request by ordinal equality. Whether resource indicators are enabled is the caller's option.
/// </para>
/// <para>
/// RFC 8707 §2 also requires an absolute URI without a fragment. SqlOS 7.2.1 does not enforce
/// that form on requests (it only binds the trimmed value), and the behavior lock records that,
/// so this type does not enforce it either. Enforcing it, together with checking the resource
/// against the client (#429), is an intended change that needs a ledger entry.
/// </para>
/// </remarks>
internal sealed record ResourceIndicator
{
    private ResourceIndicator(string value) => Value = value;

    public string Value { get; }

    /// <summary>The requested resource, or <see langword="null"/> when the request names none.</summary>
    public static ResourceIndicator? FromRequest(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : new ResourceIndicator(value.Trim());

    /// <summary>True when this resource is exactly <paramref name="audience"/>.</summary>
    public bool Matches(string? audience) => string.Equals(Value, audience, StringComparison.Ordinal);

    public override string ToString() => Value;
}
