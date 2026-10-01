using System.Text.Json.Nodes;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// The authorization-server hub as identity processes see it in layer 2: it turns
/// <see cref="LoginEvidence"/> into the 7.2.1 completion of the surface the sign-in arrived on
/// (<c>docs/architecture/domain-model.md</c> §3.7, §15 Amendment 4).
/// </summary>
/// <remarks>
/// The one implementation (<c>SqlOSHttpLoginCompletion</c>) is the temporary hub adapter. It calls
/// today's completions with the HTTP request they still need for cookies and audit context:
/// <c>CompleteCredentialSignInAsync</c> for an authorization request (#443: a credential sign-in
/// replaces a dead issuer cookie), the issuer session for the hosted AuthPage's own sign-in, and
/// <c>FinalizeClientLoginAsync</c> for a first-party client's direct login (#419). Layer 3 replaces
/// it with the hub's own processes.
/// </remarks>
internal interface ILoginCompletion
{
    Task<LoginCompletion> CompleteAsync(LoginEvidence evidence, LoginDestination destination, CancellationToken cancellationToken);
}

/// <summary>
/// The host's sign-up hook (<see cref="Configuration.SqlOSHeadlessAuthOptions.OnHeadlessSignupAsync"/>),
/// which receives the HTTP request the sign-up serves. A process asks the hook only on the surfaces
/// that called it in 7.2.1 (<see cref="SignupConventions"/>).
/// </summary>
internal interface IHostSignupHook
{
    /// <summary>True when the host configured a hook.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Runs the hook for a new account. It may refuse the sign-up by throwing (a
    /// <see cref="SqlOSHeadlessValidationException"/> names the fields), which rolls the sign-up back.
    /// </summary>
    Task RunAsync(
        SqlOSUser user,
        SqlOSAuthorizationRequest? authorizationRequest,
        SqlOSOrganization? organization,
        JsonObject customFields,
        CancellationToken cancellationToken);
}

/// <summary>
/// Where a surface asks a credential process to finish. The process resolves it into the
/// <see cref="LoginDestination"/> the hub completes once the credential is proven.
/// </summary>
internal abstract record LoginTarget
{
    private LoginTarget()
    {
    }

    /// <summary>The hosted or headless surface's authorization request.</summary>
    public sealed record AuthorizationRequest(SqlOSAuthorizationRequest Request) : LoginTarget;

    /// <summary>
    /// The hosted AuthPage with no authorization request: the browser signs in to the AuthPage
    /// itself, accepting the invitation it carries.
    /// </summary>
    public sealed record Browser(string? InvitationToken) : LoginTarget;

    /// <summary>
    /// The public API's direct login: tokens for a first-party client. <see cref="ClientId"/> is the
    /// client the request names; a credential issued to a client (a code, a link, a sign-up token)
    /// names its own, and then <see cref="ClientId"/> is null. <see cref="OrganizationId"/> is the
    /// organization the request names.
    /// </summary>
    public sealed record DirectLogin(string? ClientId = null, string? OrganizationId = null) : LoginTarget;

    /// <summary>
    /// Only the credential: the process proves it and returns its evidence, and nothing completes
    /// the login. The 7.x facades that only authenticate (<c>AuthenticatePasswordAsync</c>, the
    /// code services' <c>VerifyAsync</c>) use it; their caller completes the login itself.
    /// </summary>
    public sealed record CredentialOnly : LoginTarget
    {
        public static CredentialOnly Instance { get; } = new();
    }

    /// <summary>
    /// The destination for a browser surface: the authorization request when there is one, else
    /// the hosted AuthPage's own sign-in with the invitation the form carries.
    /// </summary>
    public static LoginTarget ForBrowser(SqlOSAuthorizationRequest? request, string? invitationToken)
        => request is null ? new Browser(invitationToken) : new AuthorizationRequest(request);
}

/// <summary>What the hub completes: a resolved <see cref="LoginTarget"/>.</summary>
internal abstract record LoginDestination
{
    private LoginDestination()
    {
    }

    /// <summary>
    /// The authorization request continues: consent, an organization choice or MFA, then the
    /// redirect to the client with a code.
    /// </summary>
    public sealed record AuthorizationRequest(SqlOSAuthorizationRequest Request) : LoginDestination;

    /// <summary>
    /// The hosted AuthPage's own sign-in: the invitation the browser carries is accepted (inside the
    /// sign-up's transaction when <see cref="InSignupTransaction"/>), then the browser gets an issuer
    /// session for the invitation's organization or the account's first one.
    /// </summary>
    public sealed record Browser(string? InvitationToken, bool InSignupTransaction) : LoginDestination;

    /// <summary>A first-party client's direct login, into <see cref="OrganizationId"/> when one is named.</summary>
    public sealed record DirectLogin(SqlOSClientApplication Client, string? OrganizationId) : LoginDestination;

    /// <summary>
    /// The authorization request whose MFA challenge the evidence answered: its code is issued in the
    /// challenge's organization, the second factor counting as fresh authentication, and a
    /// <see cref="CredentialSignIn"/> treating a dead issuer cookie as signed out (#443).
    /// </summary>
    public sealed record AuthorizationRequestAfterMfa(
        string AuthorizationRequestId,
        string? OrganizationId,
        bool CredentialSignIn) : LoginDestination;

    /// <summary>
    /// The first-party client's direct login whose MFA challenge the evidence answered: a session and
    /// tokens are issued in the challenge's organization, for its resource.
    /// </summary>
    public sealed record DirectLoginAfterMfa(
        string ClientApplicationId,
        string? OrganizationId,
        string? Resource) : LoginDestination;
}

/// <summary>What the hub did with the evidence: today's completion result, per destination.</summary>
internal abstract record LoginCompletion
{
    private LoginCompletion()
    {
    }

    /// <summary>The authorization request continues (<see cref="LoginDestination.AuthorizationRequest"/>).</summary>
    public sealed record AuthorizationRequestContinued(SqlOSAuthorizationRequestLoginResult Result) : LoginCompletion;

    /// <summary>The hosted AuthPage signed the browser in (<see cref="LoginDestination.Browser"/>).</summary>
    public sealed record BrowserSignedIn(bool AcceptedInvitation) : LoginCompletion;

    /// <summary>The direct login answered (<see cref="LoginDestination.DirectLogin"/>): tokens, an organization choice or an MFA challenge.</summary>
    public sealed record DirectLoginCompleted(SqlOSLoginResult Result) : LoginCompletion;

    /// <summary>The authorization request's code was issued (<see cref="LoginDestination.AuthorizationRequestAfterMfa"/>): the redirect to the client.</summary>
    public sealed record CodeIssued(string RedirectUrl) : LoginCompletion;

    /// <summary>The direct login's session and tokens were issued (<see cref="LoginDestination.DirectLoginAfterMfa"/>).</summary>
    public sealed record TokensIssued(SqlOSTokenResponse Tokens) : LoginCompletion;

    /// <summary>
    /// Nothing completed the login: the caller asked only for the credential's evidence
    /// (<see cref="LoginTarget.CredentialOnly"/>).
    /// </summary>
    public sealed record NotCompleted : LoginCompletion
    {
        public static NotCompleted Instance { get; } = new();
    }
}
