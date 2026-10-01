using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Processes.Identity;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// The temporary hub adapter (<c>docs/architecture/domain-model.md</c> §15 Amendment 4): turns a
/// credential process's <see cref="LoginEvidence"/> into the 7.2.1 completion of the surface the
/// sign-in arrived on, with the HTTP request that completion still needs for cookies and audit
/// context. Layer 3 replaces it with the authorization-server hub's own processes.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>An authorization request completes as a credential sign-in
/// (<see cref="SqlOSAuthorizationServerService.CompleteCredentialSignInAsync"/>): consent, the
/// bound invitation, organization choice and MFA, then the code; a revoked or cleaned-up issuer
/// cookie counts as signed out (7.2.1, #443).</item>
/// <item>The hosted AuthPage's own sign-in accepts the invitation the browser carries and gives the
/// browser an issuer session for the invitation's organization, or the account's first one.</item>
/// <item>A direct login is first-party only and returns tokens, an organization choice or an MFA
/// challenge (<see cref="SqlOSAuthService.FinalizeClientLoginAsync"/>, #419).</item>
/// <item>A login whose MFA challenge was answered continues where the hub paused it: the
/// authorization request's code is issued
/// (<see cref="SqlOSAuthorizationServerService.IssueCodeAfterMfaAsync"/>), or the direct login's
/// session and tokens (<see cref="SqlOSAuthService.IssueTokensAfterMfaAsync"/>), and
/// <c>user.login.mfa</c> is audited.</item>
/// </list>
/// It is also the host's sign-up hook, which receives the same HTTP request.
/// </remarks>
internal sealed class SqlOSHttpLoginCompletion : ILoginCompletion, IHostSignupHook
{
    private readonly HttpContext? _httpContext;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSAuthorizationServerService? _authorizationServerService;
    private readonly SqlOSAuthService? _authService;
    private readonly SqlOSIssuerSessionService? _issuerSessionService;
    private readonly SqlOSInvitationService? _invitationService;

    public SqlOSHttpLoginCompletion(
        HttpContext? httpContext,
        SqlOSAdminService adminService,
        SqlOSAuthServerOptions options,
        SqlOSAuthorizationServerService? authorizationServerService = null,
        SqlOSAuthService? authService = null,
        SqlOSIssuerSessionService? issuerSessionService = null,
        SqlOSInvitationService? invitationService = null)
    {
        _httpContext = httpContext;
        _adminService = adminService;
        _options = options;
        _authorizationServerService = authorizationServerService;
        _authService = authService;
        _issuerSessionService = issuerSessionService;
        _invitationService = invitationService;
    }

    public async Task<LoginCompletion> CompleteAsync(
        LoginEvidence evidence,
        LoginDestination destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(destination);
        return destination switch
        {
            LoginDestination.AuthorizationRequest authorizationRequest => new LoginCompletion.AuthorizationRequestContinued(
                await RequireAuthorizationServerService().CompleteCredentialSignInAsync(
                    authorizationRequest.Request,
                    evidence.User,
                    evidence.AuthenticationMethod,
                    RequireHttpContext(),
                    cancellationToken)),
            LoginDestination.Browser browser => await SignInBrowserAsync(evidence, browser, cancellationToken),
            LoginDestination.DirectLogin directLogin => new LoginCompletion.DirectLoginCompleted(
                await RequireAuthService().FinalizeClientLoginAsync(
                    evidence.User,
                    directLogin.Client,
                    directLogin.OrganizationId,
                    evidence.AuthenticationMethod,
                    RequireHttpContext(),
                    cancellationToken)),
            LoginDestination.AuthorizationRequestAfterMfa afterMfa => new LoginCompletion.CodeIssued(
                await RequireAuthorizationServerService().IssueCodeAfterMfaAsync(
                    afterMfa.AuthorizationRequestId,
                    evidence.User,
                    afterMfa.OrganizationId,
                    evidence.AuthenticationMethod,
                    evidence.AuthenticatedAt,
                    afterMfa.CredentialSignIn,
                    RequireHttpContext(),
                    cancellationToken)),
            // A host may answer a direct login's challenge from its own code, without a request.
            LoginDestination.DirectLoginAfterMfa afterMfa => new LoginCompletion.TokensIssued(
                await RequireAuthService().IssueTokensAfterMfaAsync(
                    evidence.User,
                    afterMfa.ClientApplicationId,
                    afterMfa.OrganizationId,
                    evidence.AuthenticationMethod,
                    afterMfa.Resource,
                    _httpContext,
                    cancellationToken)),
            _ => throw new InvalidOperationException($"Unknown login destination '{destination.GetType().Name}'.")
        };
    }

    public bool IsConfigured => _options.Headless.OnHeadlessSignupAsync != null;

    public Task RunAsync(
        SqlOSUser user,
        SqlOSAuthorizationRequest? authorizationRequest,
        SqlOSOrganization? organization,
        JsonObject customFields,
        CancellationToken cancellationToken)
        => _options.Headless.OnHeadlessSignupAsync is { } hook
            ? hook(new SqlOSHeadlessSignupHookContext(RequireHttpContext(), authorizationRequest, user, organization, customFields), cancellationToken)
            : Task.CompletedTask;

    private async Task<LoginCompletion> SignInBrowserAsync(
        LoginEvidence evidence,
        LoginDestination.Browser browser,
        CancellationToken cancellationToken)
    {
        var organizations = await _adminService.GetUserOrganizationsAsync(evidence.UserId, cancellationToken);
        var organizationId = organizations.FirstOrDefault()?.Id;
        var acceptedInvitation = false;
        if (!string.IsNullOrWhiteSpace(browser.InvitationToken))
        {
            var invitations = _invitationService ?? throw new InvalidOperationException("SqlOS invitations are not configured.");
            var request = new SqlOSAcceptEmailInvitationRequest(browser.InvitationToken, evidence.UserId)
            {
                AuthenticationMethod = evidence.AuthenticationMethod
            };
            var acceptance = browser.InSignupTransaction
                ? await invitations.AcceptEmailInvitationInCurrentTransactionAsync(request, RequireHttpContext(), cancellationToken)
                : await invitations.AcceptEmailInvitationAsync(request, RequireHttpContext(), cancellationToken);
            organizationId = acceptance.OrganizationId;
            acceptedInvitation = true;
        }

        var issuerSessions = _issuerSessionService ?? throw new InvalidOperationException("Issuer session support is not configured.");
        await issuerSessions.SignInAsync(RequireHttpContext(), evidence.User, organizationId, evidence.AuthenticationMethod, cancellationToken);
        return new LoginCompletion.BrowserSignedIn(acceptedInvitation);
    }

    /// <summary>The completions set cookies and read the request; a facade called without one cannot complete a login.</summary>
    private HttpContext RequireHttpContext()
        => _httpContext ?? throw new InvalidOperationException("Completing a sign-in needs the HTTP request it answers.");

    private SqlOSAuthorizationServerService RequireAuthorizationServerService()
        => _authorizationServerService ?? throw new InvalidOperationException("The authorization server is not configured.");

    private SqlOSAuthService RequireAuthService()
        => _authService ?? throw new InvalidOperationException("Auth service is not registered.");
}
