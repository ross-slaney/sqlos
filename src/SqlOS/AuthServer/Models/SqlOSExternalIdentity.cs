using SqlOS.Domain;

namespace SqlOS.AuthServer.Models;

/// <summary>
/// An account at an upstream identity provider linked to a <see cref="SqlOSUser"/>: a custom or
/// social OpenID connection (<see cref="OidcConnectionId"/>) or an organization's SAML connection
/// (<see cref="SsoConnectionId"/>), and the provider's subject for the person.
/// </summary>
/// <remarks>
/// Part of the <see cref="SqlOSUser"/> aggregate. An identity is linked when it registers the
/// account, or to an existing account only with an <see cref="OwnershipProof"/> from its provider
/// for one of the account's verified addresses (#423); it is unlinked only by a claim of the
/// account's address, which deletes the row.
/// </remarks>
public sealed class SqlOSExternalIdentity
{
    private SqlOSExternalIdentity()
    {
    }

    public string Id { get; private set; } = string.Empty;
    public string UserId { get; private set; } = string.Empty;
    public string? SsoConnectionId { get; private set; }
    public string? OidcConnectionId { get; private set; }
    public string Issuer { get; private set; } = string.Empty;
    public string Subject { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public SqlOSUser? User { get; private set; }
    public SqlOSSsoConnection? SsoConnection { get; private set; }
    public SqlOSOidcConnection? OidcConnection { get; private set; }

    /// <summary><c>oidc</c> or <c>saml</c>, as the 7.2.1 audit names an identity's kind.</summary>
    internal string Kind => OidcConnectionId != null ? ExternalIdentityLink.OidcKind : ExternalIdentityLink.SamlKind;

    /// <summary>The OpenID or SAML connection the identity came through.</summary>
    internal string? ConnectionId => OidcConnectionId ?? SsoConnectionId;

    internal static SqlOSExternalIdentity Link(string userId, ExternalIdentityLink link, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(link);
        return new SqlOSExternalIdentity
        {
            Id = SqlOSIds.New("ext"),
            UserId = userId,
            OidcConnectionId = link.Kind == ExternalIdentityKind.Oidc ? link.ConnectionId : null,
            SsoConnectionId = link.Kind == ExternalIdentityKind.Saml ? link.ConnectionId : null,
            Issuer = link.Issuer,
            Subject = link.Subject,
            Email = link.Email,
            CreatedAt = now
        };
    }
}

/// <summary>The kind of upstream an external identity comes from.</summary>
internal enum ExternalIdentityKind
{
    /// <summary>A custom or social OpenID connection.</summary>
    Oidc = 1,

    /// <summary>An organization's SAML connection.</summary>
    Saml = 2
}

/// <summary>
/// An upstream account to link: its connection, the issuer and subject the provider asserted, and
/// the email it asserted as received. <see cref="Provider"/> names an OpenID connection's provider
/// type for the provisioning audit.
/// </summary>
internal sealed record ExternalIdentityLink
{
    internal const string OidcKind = "oidc";
    internal const string SamlKind = "saml";

    private ExternalIdentityLink(
        ExternalIdentityKind kind,
        string connectionId,
        string issuer,
        string subject,
        string? email,
        string? provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(subject);
        Kind = kind;
        ConnectionId = connectionId;
        Issuer = issuer;
        Subject = subject;
        Email = email;
        Provider = provider;
    }

    public ExternalIdentityKind Kind { get; }
    public string ConnectionId { get; }
    public string Issuer { get; }
    public string Subject { get; }
    public string? Email { get; }
    public string? Provider { get; }

    /// <summary>An account at the OpenID connection <paramref name="oidcConnectionId"/> of provider type <paramref name="provider"/>.</summary>
    public static ExternalIdentityLink Oidc(string oidcConnectionId, string provider, string issuer, string subject, string? email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return new ExternalIdentityLink(ExternalIdentityKind.Oidc, oidcConnectionId, issuer, subject, email, provider);
    }

    /// <summary>A subject asserted by the SAML connection <paramref name="ssoConnectionId"/>.</summary>
    public static ExternalIdentityLink Saml(string ssoConnectionId, string issuer, string subject, string? email)
        => new(ExternalIdentityKind.Saml, ssoConnectionId, issuer, subject, email, provider: null);

    /// <summary>
    /// True when <paramref name="proof"/> came from this identity's kind of upstream: an OpenID
    /// provider's proof links only an OpenID identity, a SAML assertion's only a SAML one.
    /// </summary>
    internal bool IsProvenBy(OwnershipProof proof)
        => Kind switch
        {
            ExternalIdentityKind.Oidc => proof.Method == OwnershipProofMethod.Oidc,
            ExternalIdentityKind.Saml => proof.Method == OwnershipProofMethod.Saml,
            _ => false
        };

    internal string KindName => Kind == ExternalIdentityKind.Oidc ? OidcKind : SamlKind;
}
