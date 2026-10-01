namespace SqlOS.Domain;

/// <summary>
/// This person controls this mailbox: a completed challenge, or an upstream that may vouch for
/// it, proved that whoever is signing in receives mail at <see cref="Address"/>
/// (<c>docs/architecture/domain-model.md</c> §3.5).
/// </summary>
/// <remarks>
/// <para>
/// Only the listed producers construct a proof (<c>proof-producers.txt</c>, enforced by the
/// architecture tests). A completed email-code challenge produces one for its stored recipient;
/// the sign-in link, password-reset link, email-verification link, invitation, upstream OIDC,
/// SAML and SCIM paths, and the email-code and invitation sign-ups, produce one where 7.2.1
/// decided the mailbox was proven, until their own processes and aggregates take that decision
/// over (layers 2 to 4).
/// </para>
/// <para>
/// The <c>SqlOSUser</c> aggregate is the consumer: an address becomes verified only with a proof
/// (<c>VerifyEmail</c>, <c>ClaimWithProof</c>, registration with a proven address,
/// <c>SetPrimaryEmail</c>), and an external identity is linked to an existing account only with
/// one (<c>LinkExternalIdentity</c>). Consumers take the proof, never an address or a flag.
/// </para>
/// <para>
/// A proof is for one mailbox, compared by the canonical <see cref="EmailAddress"/> key (#422),
/// so a proof for one spelling covers every spelling of the same mailbox and nothing else.
/// </para>
/// </remarks>
internal sealed class OwnershipProof : ISqlOSProof
{
    /// <summary>The 7.2.1 message for a proof presented for an address it does not cover.</summary>
    internal const string MismatchMessage = "The ownership proof is for another mailbox.";

    internal OwnershipProof(EmailAddress address, OwnershipProofMethod method)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown ownership proof method.");
        }

        Address = address;
        Method = method;
    }

    /// <summary>The proven mailbox.</summary>
    public EmailAddress Address { get; }

    /// <summary>How the mailbox was proven.</summary>
    public OwnershipProofMethod Method { get; }

    /// <summary>
    /// The method as the 7.2.1 audit records it (the <c>proof</c> of <c>user.email.claimed</c>).
    /// </summary>
    public string MethodName => NameOf(Method);

    /// <summary>The name the audit records for <paramref name="method"/>.</summary>
    public static string NameOf(OwnershipProofMethod method) => method switch
    {
        OwnershipProofMethod.EmailOtp => "email_otp",
        OwnershipProofMethod.MagicLink => "magic_link",
        OwnershipProofMethod.PasswordReset => "password_reset",
        OwnershipProofMethod.Invitation => "invitation",
        OwnershipProofMethod.Oidc => "oidc",
        OwnershipProofMethod.Saml => "saml",
        OwnershipProofMethod.EmailVerification => "email_verification",
        OwnershipProofMethod.Directory => "scim",
        _ => throw new InvalidOperationException($"Unknown ownership proof method '{method}'.")
    };

    /// <summary>True when <paramref name="storedAddress"/> is the proven mailbox.</summary>
    public bool Covers(string? storedAddress) => Address.MatchesStored(storedAddress);

    public override string ToString() => $"{nameof(OwnershipProof)}({MethodName})";
}

/// <summary>How an <see cref="OwnershipProof"/> proved a mailbox.</summary>
internal enum OwnershipProofMethod
{
    /// <summary>A code sent to the mailbox came back (email-code sign-in or sign-up).</summary>
    EmailOtp = 1,

    /// <summary>A sign-in link sent to the mailbox was opened.</summary>
    MagicLink = 2,

    /// <summary>A password-reset link sent to the mailbox was completed.</summary>
    PasswordReset = 3,

    /// <summary>An invitation sent to the mailbox was accepted.</summary>
    Invitation = 4,

    /// <summary>An upstream OpenID provider vouched for the address as verified (#423).</summary>
    Oidc = 5,

    /// <summary>A SAML assertion from the organization that owns the address's domain (#420).</summary>
    Saml = 6,

    /// <summary>
    /// The email-verification link sent to the mailbox was opened. It confirms the address an
    /// account registered with and, unlike a sign-in, never claims it.
    /// </summary>
    EmailVerification = 7,

    /// <summary>
    /// A SCIM directory set the address as the person's primary email inside a domain its
    /// organization verified (#420). A directory never claims an address.
    /// </summary>
    Directory = 8
}
