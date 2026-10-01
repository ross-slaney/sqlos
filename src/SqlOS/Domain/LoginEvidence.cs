using SqlOS.AuthServer.Models;

namespace SqlOS.Domain;

/// <summary>
/// This user authenticated, by these methods, at this time: what a credential process proves and
/// the authorization-server hub consumes to sign the person in
/// (<c>docs/architecture/domain-model.md</c> §3.5).
/// </summary>
/// <remarks>
/// <para>
/// Only the credential processes construct evidence (<c>proof-producers.txt</c>, enforced by the
/// architecture tests): password sign-in, the email-code, sign-in link and phone-code sign-ins, and
/// every sign-up, each once its own checks passed. In layer 2 the temporary hub adapter
/// (<c>ILoginCompletion</c>) passes the evidence to the 7.2.1 completion; layer 3's hub consumes it
/// directly (the authorization request, direct login).
/// </para>
/// <para>
/// Evidence is always fresh: a credential the person presented in this flow. The issuer session a
/// browser presents is not login evidence but a session the hub continues (7.2.1, #443).
/// </para>
/// </remarks>
internal sealed class LoginEvidence : ISqlOSProof
{
    internal LoginEvidence(
        SqlOSUser user,
        IEnumerable<string> methods,
        IEnumerable<OwnershipProof> proofs,
        DateTime authenticatedAt)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(proofs);

        var methodList = methods.ToArray();
        if (methodList.Length == 0)
        {
            throw new ArgumentException("Login evidence names at least one authentication method.", nameof(methods));
        }

        foreach (var method in methodList)
        {
            if (string.IsNullOrWhiteSpace(method)
                || method.Any(static character => character is '+' or ',' || char.IsWhiteSpace(character)))
            {
                throw new ArgumentException($"'{method}' is not an authentication method name.", nameof(methods));
            }
        }

        if (methodList.Distinct(StringComparer.OrdinalIgnoreCase).Count() != methodList.Length)
        {
            throw new ArgumentException("An authentication method is named once.", nameof(methods));
        }

        var proofList = proofs.ToArray();
        if (proofList.Any(static proof => proof is null))
        {
            throw new ArgumentException("A proof cannot be null.", nameof(proofs));
        }

        User = user;
        Methods = methodList;
        Proofs = proofList;
        AuthenticatedAt = authenticatedAt;
    }

    /// <summary>The account that authenticated (the tracked aggregate).</summary>
    public SqlOSUser User { get; }

    public string UserId => User.Id;

    /// <summary>
    /// The authentication methods, in the order they were completed (the <c>amr</c>): the first
    /// factor, then any second factor.
    /// </summary>
    public IReadOnlyList<string> Methods { get; }

    /// <summary>
    /// The methods as the 7.x authentication-method string sessions, codes and audit rows record:
    /// joined with <c>+</c> (<c>password</c>, <c>password+totp</c>).
    /// </summary>
    public string AuthenticationMethod => string.Join('+', Methods);

    /// <summary>One factor, or a first factor and a second one.</summary>
    public LoginAssurance Assurance => Methods.Count > 1 ? LoginAssurance.MultiFactor : LoginAssurance.SingleFactor;

    /// <summary>
    /// The mailboxes the login proved along the way: the address an email code or a sign-in link
    /// was delivered to, or the address a sign-up's code or invitation proved.
    /// </summary>
    public IReadOnlyList<OwnershipProof> Proofs { get; }

    /// <summary>When the person authenticated: the instant the producing process read.</summary>
    public DateTime AuthenticatedAt { get; }

    public override string ToString() => $"{nameof(LoginEvidence)}({AuthenticationMethod})";
}

/// <summary>How strongly a <see cref="LoginEvidence"/> authenticates its user.</summary>
internal enum LoginAssurance
{
    /// <summary>
    /// One factor: a password, or a code or link delivered to an address or phone the account
    /// owns, or a sign-up's proven address.
    /// </summary>
    SingleFactor = 1,

    /// <summary>A first factor completed by a second (an authenticator code or a recovery code).</summary>
    MultiFactor = 2
}

/// <summary>
/// The authentication-method names 7.x records for each credential (session, authorization code and
/// <c>user.login.*</c> audit rows).
/// </summary>
internal static class AuthenticationMethods
{
    public const string Password = "password";
    public const string EmailOtp = "email_otp";
    public const string MagicLink = "magic_link";
    public const string PhoneOtp = "phone_otp";
    public const string Invitation = "invitation";
}
