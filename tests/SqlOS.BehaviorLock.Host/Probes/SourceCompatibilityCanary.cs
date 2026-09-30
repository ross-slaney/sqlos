using System.Linq.Expressions;
using Microsoft.AspNetCore.Http;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Calendar.Contracts;
using SqlOS.Calendar.Services;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;
using SqlOS.Extensions;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.BehaviorLock.Host.Probes;

/// <summary>
/// Compile-time canary for the public service members that the documentation and examples
/// tell hosts to call. Each entry pins one member's parameter and return types; nothing here
/// runs. <see cref="ProbeEndpoints"/> executes the most important ones; this file makes every
/// other documented signature change a compile error in source mode, so a refactor layer has to
/// notice it, record it in the behavior ledger, and keep package mode compiling (for example with
/// <c>#if SQLOS_UNDER_TEST_PACKAGE</c>).
/// </summary>
/// <remarks>
/// The member list comes from <c>web/content/docs</c>, <c>docs/</c>, and <c>examples/</c>
/// (see <c>docs/architecture/public-api-inventory.md</c> for the per-type evidence). Configuration
/// APIs (<c>AddSqlOS</c>, options, seeds) are pinned by the profiles in <c>Profiles/HostProfiles.cs</c>.
/// </remarks>
internal static class SourceCompatibilityCanary
{
    // SqlOSAuthService: sign-in, sign-up, sessions, invitations, device flow, MFA.
    internal static readonly Func<SqlOSAuthService, SqlOSSignupRequest, HttpContext, CancellationToken, Task<SqlOSLoginResult>> SignUp =
        (service, request, http, ct) => service.SignUpAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSPasswordLoginRequest, HttpContext, CancellationToken, Task<SqlOSLoginResult>> LoginWithPassword =
        (service, request, http, ct) => service.LoginWithPasswordAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSSelectOrganizationRequest, HttpContext, CancellationToken, Task<SqlOSLoginResult>> SelectOrganizationForLogin =
        (service, request, http, ct) => service.SelectOrganizationForLoginAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSRefreshRequest, CancellationToken, Task<SqlOSTokenResponse>> Refresh =
        (service, request, ct) => service.RefreshAsync(request, ct);
    internal static readonly Func<SqlOSAuthService, string?, string?, CancellationToken, Task> Logout =
        (service, refreshToken, sessionId, ct) => service.LogoutAsync(refreshToken, sessionId, ct);
    internal static readonly Func<SqlOSAuthService, string, CancellationToken, Task> LogoutAll =
        (service, userId, ct) => service.LogoutAllAsync(userId, ct);
    internal static readonly Func<SqlOSAuthService, string, string, CancellationToken, Task<SqlOSValidatedToken?>> ValidateAccessToken =
        (service, token, audience, ct) => service.ValidateAccessTokenAsync(token, audience, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSEmailOtpStartRequest, HttpContext?, CancellationToken, Task<SqlOSEmailOtpStartResult>> RequestEmailOtp =
        (service, request, http, ct) => service.RequestEmailOtpAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSEmailOtpVerifyRequest, HttpContext, CancellationToken, Task<SqlOSLoginResult>> VerifyEmailOtp =
        (service, request, http, ct) => service.VerifyEmailOtpAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSEmailOtpSignupStartRequest, HttpContext?, CancellationToken, Task<SqlOSEmailOtpSignupStartResult>> RequestEmailOtpSignup =
        (service, request, http, ct) => service.RequestEmailOtpSignupAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSEmailOtpSignupVerifyRequest, HttpContext, CancellationToken, Task<SqlOSLoginResult>> VerifyEmailOtpSignup =
        (service, request, http, ct) => service.VerifyEmailOtpSignupAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSPhoneOtpStartRequest, HttpContext?, CancellationToken, Task<SqlOSPhoneOtpStartResult>> RequestPhoneOtp =
        (service, request, http, ct) => service.RequestPhoneOtpAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSPhoneOtpVerifyRequest, HttpContext, CancellationToken, Task<SqlOSLoginResult>> VerifyPhoneOtp =
        (service, request, http, ct) => service.VerifyPhoneOtpAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSPhoneOtpSignupStartRequest, HttpContext?, CancellationToken, Task<SqlOSPhoneOtpSignupStartResult>> RequestPhoneOtpSignup =
        (service, request, http, ct) => service.RequestPhoneOtpSignupAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSPhoneOtpSignupVerifyRequest, HttpContext, CancellationToken, Task<SqlOSLoginResult>> VerifyPhoneOtpSignup =
        (service, request, http, ct) => service.VerifyPhoneOtpSignupAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSMagicLinkStartRequest, HttpContext?, CancellationToken, Task<SqlOSMagicLinkStartResult>> RequestMagicLink =
        (service, request, http, ct) => service.RequestMagicLinkAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSMagicLinkCompleteRequest, HttpContext, CancellationToken, Task<SqlOSLoginResult>> CompleteMagicLink =
        (service, request, http, ct) => service.CompleteMagicLinkAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSForgotPasswordRequest, HttpContext?, CancellationToken, Task<SqlOSPasswordResetRequestResult>> RequestPasswordResetEmail =
        (service, request, http, ct) => service.RequestPasswordResetEmailAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSForgotPasswordRequest, CancellationToken, Task<string>> CreatePasswordResetToken =
        (service, request, ct) => service.CreatePasswordResetTokenAsync(request, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSSendPasswordResetEmailRequest, HttpContext?, CancellationToken, Task<SqlOSPasswordResetEmailResult>> SendPasswordResetEmail =
        (service, request, http, ct) => service.SendPasswordResetEmailAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSResetPasswordRequest, CancellationToken, Task> ResetPassword =
        (service, request, ct) => service.ResetPasswordAsync(request, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSCreateVerificationTokenRequest, CancellationToken, Task<string>> CreateEmailVerificationToken =
        (service, request, ct) => service.CreateEmailVerificationTokenAsync(request, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSVerifyEmailRequest, CancellationToken, Task> VerifyEmail =
        (service, request, ct) => service.VerifyEmailAsync(request, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSCreateEmailInvitationRequest, HttpContext?, CancellationToken, Task<SqlOSEmailInvitationResult>> CreateEmailInvitation =
        (service, request, http, ct) => service.CreateEmailInvitationAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSResendEmailInvitationRequest, HttpContext?, CancellationToken, Task<SqlOSEmailInvitationResult>> ResendEmailInvitation =
        (service, request, http, ct) => service.ResendEmailInvitationAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSRevokeEmailInvitationRequest, HttpContext?, CancellationToken, Task<SqlOSEmailInvitationResult>> RevokeEmailInvitation =
        (service, request, http, ct) => service.RevokeEmailInvitationAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSAcceptEmailInvitationRequest, HttpContext?, CancellationToken, Task<SqlOSInvitationAcceptanceResult>> AcceptEmailInvitation =
        (service, request, http, ct) => service.AcceptEmailInvitationAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSAcceptEmailInvitationSignupRequest, HttpContext, CancellationToken, Task<SqlOSLoginResult>> AcceptEmailInvitationSignup =
        (service, request, http, ct) => service.AcceptEmailInvitationSignupAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSDeviceAuthorizationStartRequest, HttpContext, CancellationToken, Task<SqlOSDeviceAuthorizationStartResult>> StartDeviceAuthorization =
        (service, request, http, ct) => service.StartDeviceAuthorizationAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, string, SqlOSUser?, CancellationToken, Task<SqlOSDeviceAuthorizationResolveResult>> ResolveDeviceAuthorization =
        (service, userCode, user, ct) => service.ResolveDeviceAuthorizationAsync(userCode, user, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSDeviceAuthorizationApprovalRequest, SqlOSUser, string, HttpContext, CancellationToken, Task<SqlOSDeviceAuthorizationResolveResult>> ApproveDeviceAuthorization =
        (service, request, user, method, http, ct) => service.ApproveDeviceAuthorizationAsync(request, user, method, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSDeviceTokenPollRequest, HttpContext, CancellationToken, Task<SqlOSDeviceTokenPollResult>> PollDeviceAuthorization =
        (service, request, http, ct) => service.PollDeviceAuthorizationAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, string, string?, CancellationToken, Task<SqlOSMfaStatusResult>> GetMfaStatus =
        (service, userId, organizationId, ct) => service.GetMfaStatusAsync(userId, organizationId, ct);
    internal static readonly Func<SqlOSAuthService, string, CancellationToken, Task<IReadOnlyList<SqlOSMfaAuthenticatorDto>>> ListMfaAuthenticators =
        (service, userId, ct) => service.ListMfaAuthenticatorsAsync(userId, ct);
    internal static readonly Func<SqlOSAuthService, string, SqlOSTotpEnrollmentStartRequest, string?, CancellationToken, Task<SqlOSTotpEnrollmentStartResult>> StartTotpEnrollment =
        (service, userId, request, organizationId, ct) => service.StartTotpEnrollmentAsync(userId, request, organizationId, ct);
    internal static readonly Func<SqlOSAuthService, string, SqlOSTotpEnrollmentStartRequest, CancellationToken, Task<SqlOSTotpEnrollmentStartResult>> StartTotpEnrollmentForChallenge =
        (service, mfaToken, request, ct) => service.StartTotpEnrollmentForChallengeAsync(mfaToken, request, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSTotpEnrollmentVerifyRequest, HttpContext?, CancellationToken, Task<SqlOSTotpEnrollmentVerifyResult>> VerifyTotpEnrollment =
        (service, request, http, ct) => service.VerifyTotpEnrollmentAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSMfaChallengeVerifyRequest, HttpContext?, CancellationToken, Task<SqlOSMfaChallengeVerifyResult>> VerifyMfaChallenge =
        (service, request, http, ct) => service.VerifyMfaChallengeAsync(request, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSUser, SqlOSClientApplication, string, HttpContext, CancellationToken, Task<SqlOSLoginResult>> CompleteExternalLogin =
        (service, user, client, method, http, ct) => service.CompleteExternalLoginAsync(user, client, method, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSUser, SqlOSClientApplication, string?, string, HttpContext, CancellationToken, Task<SqlOSLoginResult>> CompleteClientAuthentication =
        (service, user, client, organizationId, method, http, ct) => service.CompleteClientAuthenticationAsync(user, client, organizationId, method, http, ct);
    internal static readonly Func<SqlOSAuthService, SqlOSUser, SqlOSClientApplication, string?, string, string?, string?, CancellationToken, Task<SqlOSTokenResponse>> CreateSessionTokensForUser =
        (service, user, client, organizationId, method, userAgent, ipAddress, ct) =>
            service.CreateSessionTokensForUserAsync(user, client, organizationId, method, userAgent, ipAddress, ct);

    // SqlOSAdminService: provisioning, application access, connections, audit.
    internal static readonly Func<SqlOSAdminService, SqlOSCreateUserRequest, CancellationToken, Task<SqlOSUser>> CreateUser =
        (service, request, ct) => service.CreateUserAsync(request, ct);
    internal static readonly Func<SqlOSAdminService, SqlOSCreateOrganizationRequest, CancellationToken, Task<SqlOSOrganization>> CreateOrganization =
        (service, request, ct) => service.CreateOrganizationAsync(request, ct);
    internal static readonly Func<SqlOSAdminService, string, SqlOSCreateMembershipRequest, CancellationToken, Task<SqlOSMembership>> CreateMembership =
        (service, organizationId, request, ct) => service.CreateMembershipAsync(organizationId, request, ct);
    internal static readonly Func<SqlOSAdminService, SqlOSCreateClientRequest, CancellationToken, Task<SqlOSClientApplication>> CreateClient =
        (service, request, ct) => service.CreateClientAsync(request, ct);
    internal static readonly Func<SqlOSAdminService, string, SqlOSSetApplicationAccessModeRequest, string, string?, CancellationToken, Task<SqlOSClientApplication>> SetApplicationAccessMode =
        (service, clientId, request, actorType, actorId, ct) => service.SetApplicationAccessModeAsync(clientId, request, actorType, actorId, ct);
    internal static readonly Func<SqlOSAdminService, string, SqlOSCreateApplicationAssignmentRequest, string, string?, CancellationToken, Task<SqlOSApplicationAssignment>> AssignApplication =
        (service, clientId, request, actorType, actorId, ct) => service.AssignApplicationAsync(clientId, request, actorType, actorId, ct);
    internal static readonly Func<SqlOSAdminService, string, string?, string?, CancellationToken, Task<SqlOSApplicationAccessCheckResult>> CheckApplicationAccess =
        (service, clientId, userId, organizationId, ct) => service.CheckApplicationAccessAsync(clientId, userId, organizationId, ct);
    internal static readonly Func<SqlOSAdminService, SqlOSCreateSsoConnectionDraftRequest, CancellationToken, Task<SqlOSSsoConnection>> CreateSsoConnectionDraft =
        (service, request, ct) => service.CreateSsoConnectionDraftAsync(request, ct);
    internal static readonly Func<SqlOSAdminService, string, SqlOSImportSsoMetadataRequest, CancellationToken, Task<SqlOSSsoConnection>> ImportSsoMetadata =
        (service, connectionId, request, ct) => service.ImportSsoMetadataAsync(connectionId, request, ct);
    internal static readonly Func<SqlOSAdminService, SqlOSCreateScimConnectionRequest, CancellationToken, Task<SqlOSCreateScimConnectionResult>> CreateScimConnection =
        (service, request, ct) => service.CreateScimConnectionAsync(request, ct);
    internal static readonly Func<SqlOSAdminService, SqlOSCreateOidcConnectionRequest, CancellationToken, Task<SqlOSOidcConnection>> CreateOidcConnection =
        (service, request, ct) => service.CreateOidcConnectionAsync(request, ct);
    internal static readonly Func<SqlOSAdminService, string, string, CancellationToken, Task<bool>> UserHasMembership =
        (service, userId, organizationId, ct) => service.UserHasMembershipAsync(userId, organizationId, ct);
    internal static readonly Func<SqlOSAdminService, string, CancellationToken, Task<List<SqlOSOrganizationOption>>> GetUserOrganizations =
        (service, userId, ct) => service.GetUserOrganizationsAsync(userId, ct);
    internal static readonly Func<SqlOSAdminService, string, string?, int?, int?, CancellationToken, Task<object>> ListUserSessions =
        (service, userId, cursor, pageSize, page, ct) => service.ListUserSessionsAsync(userId, cursor, pageSize, page, ct);
    internal static readonly Func<SqlOSAdminService, string, string, string?, string?, string?, string?, string?, object?, CancellationToken, Task> RecordAudit =
        (service, eventType, actorType, actorId, userId, organizationId, sessionId, ipAddress, data, ct) =>
            service.RecordAuditAsync(eventType, actorType, actorId, userId, organizationId, sessionId, ipAddress, data, ct);

    // Social, OIDC, and SAML browser flows.
    internal static readonly Func<SqlOSOidcAuthService, CancellationToken, Task<IReadOnlyList<SqlOSOidcProviderSummary>>> ListEnabledProviders =
        (service, ct) => service.ListEnabledProvidersAsync(ct);
    internal static readonly Func<SqlOSOidcAuthService, SqlOSStartOidcAuthorizationRequest, string?, CancellationToken, Task<SqlOSStartOidcAuthorizationResult>> StartOidcAuthorization =
        (service, request, ipAddress, ct) => service.StartAuthorizationAsync(request, ipAddress, ct);
    internal static readonly Func<SqlOSOidcAuthService, SqlOSCompleteOidcAuthorizationRequest, string?, CancellationToken, Task<SqlOSCompleteOidcAuthorizationResult>> CompleteOidcAuthorization =
        (service, request, ipAddress, ct) => service.CompleteAuthorizationAsync(request, ipAddress, ct);
    internal static readonly Func<SqlOSOidcBrowserAuthService, SqlOSOidcAuthorizationUrlRequest, HttpContext, CancellationToken, Task<SqlOSOidcAuthorizationUrlResult>> CreateOidcBrowserAuthorizationUrl =
        (service, request, http, ct) => service.CreateAuthorizationUrlAsync(request, http, ct);
    internal static readonly Func<SqlOSOidcBrowserAuthService, SqlOSPkceExchangeRequest, HttpContext, CancellationToken, Task<SqlOSLoginResult>> ExchangeOidcBrowserCode =
        (service, request, http, ct) => service.ExchangeCodeAsync(request, http, ct);
    internal static readonly Func<SqlOSSamlService, SqlOSAuthorizationUrlRequest, CancellationToken, Task<string>> CreateSamlAuthorizationUrl =
        (service, request, ct) => service.CreateAuthorizationUrlAsync(request, ct);
    internal static readonly Func<SqlOSSsoAuthorizationService, SqlOSSsoAuthorizationStartRequest, CancellationToken, Task<SqlOSSsoAuthorizationStartResult>> StartSsoAuthorization =
        (service, request, ct) => service.StartAuthorizationAsync(request, ct);

    // Token validation from a same-process route.
    internal static readonly Func<HttpContext, SqlOSValidatedToken?> ValidatedTokenFromContext =
        context => context.GetSqlOSValidatedToken();

    // FGA read path and resource/grant helpers.
    internal static readonly Func<ISqlOSFgaAuthService, string, string, string, Task<SqlOSFgaAccessCheckResult>> CheckAccess =
        (service, subjectId, permissionKey, resourceId) => service.CheckAccessAsync(subjectId, permissionKey, resourceId);
    internal static readonly Func<ISqlOSFgaAuthService, string, string, Task<bool>> HasCapability =
        (service, subjectId, permissionKey) => service.HasCapabilityAsync(subjectId, permissionKey);
    internal static readonly Func<ISqlOSFgaAuthService, string, string, string, Task<SqlOSFgaResourceAccessTrace>> TraceResourceAccess =
        (service, subjectId, resourceId, permissionKey) => service.TraceResourceAccessAsync(subjectId, resourceId, permissionKey);
    internal static readonly Func<ISqlOSFgaAuthService, string, string, Task<Expression<Func<Workspace, bool>>>> BuildFilter =
        (service, subjectId, permissionKey) => service.BuildFilterAsync<Workspace>(subjectId, permissionKey);
    internal static readonly Func<ISqlOSFgaAuthService, IQueryable<Workspace>, Expression<Func<Workspace, bool>>, string, string, Func<Workspace, object>, Task<IResult>> AuthorizedDetail =
        (service, query, predicate, subjectId, permissionKey, selector) => service.AuthorizedDetailAsync(query, predicate, subjectId, permissionKey, selector);
    internal static readonly Func<ISqlOSFgaDbContext, string, string, string, string?, string> CreateResource =
        (context, parentId, name, resourceTypeId, id) => context.CreateResource(parentId, name, resourceTypeId, id);
    internal static readonly Func<ISqlOSFgaDbContext, string, ISqlOSResourceEntity, string, CancellationToken, Task<SqlOSFgaGrant>> GrantRoleOnEntity =
        (context, subjectId, resource, role, ct) => context.GrantRoleAsync(subjectId, resource, role, ct);

    // Companion modules.
    internal static readonly Func<ISqlOSAuditLogService, SqlOSAuditLogRecordRequest, CancellationToken, Task<SqlOSAuditLogRecordResult>> RecordAuditEvent =
        (service, request, ct) => service.RecordAsync(request, ct);
    internal static readonly Func<ISqlOSAuditLogService, SqlOSAuditLogListRequest, CancellationToken, Task<SqlOSAuditLogListResult>> ListAuditEvents =
        (service, request, ct) => service.ListAsync(request, ct);
    internal static readonly Func<ISqlOSTransactionalEmailService, SqlOSSendEmailRequest, CancellationToken, Task<SqlOSSendEmailResult>> SendTransactionalEmail =
        (service, request, ct) => service.SendAsync(request, ct);
    internal static readonly Func<SqlOSCalendarService, SqlOSStartCalendarConnectRequest, HttpContext?, CancellationToken, Task<SqlOSStartCalendarConnectResult>> StartCalendarConnect =
        (service, request, http, ct) => service.StartConnectAsync(request, http, ct);
    internal static readonly Func<SqlOSCalendarService, string?, string?, bool, CancellationToken, Task<IReadOnlyList<SqlOSCalendarConnectionSummary>>> ListCalendarConnections =
        (service, userId, organizationId, includeRevoked, ct) => service.ListConnectionsAsync(userId, organizationId, includeRevoked, ct);
}
