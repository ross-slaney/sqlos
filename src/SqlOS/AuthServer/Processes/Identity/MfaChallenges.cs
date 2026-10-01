using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// The MFA challenge as the identity processes that answer it see it: the challenge the hub issued
/// between a first factor and the second (a temporary token of the MFA-challenge kind), the login it
/// pauses, and the refusals its answers share.
/// </summary>
/// <remarks>
/// A challenge belongs to one login: a first-party client's direct login (<see cref="Client"/>
/// flow, the public API) or an authorization request (<see cref="Authorization"/> flow, the hosted
/// AuthPage and the headless API). A challenge whose policy requires enrollment is answered only by
/// confirming an authenticator bound to it (<see cref="VerifyTotpEnrollment"/>); every other
/// challenge is answered with a second factor (<see cref="VerifyMfaChallenge"/>).
/// </remarks>
internal static class MfaChallenges
{
    /// <summary>The flow of a challenge issued for a first-party client's direct login.</summary>
    public const string Client = "client";

    /// <summary>The flow of a challenge issued for an authorization request.</summary>
    public const string Authorization = "authorization";

    public static readonly IdentityRefusal Invalid = new("mfa_challenge_invalid", "MFA challenge is invalid or expired.");
    public static readonly IdentityRefusal PayloadInvalid = new("mfa_challenge_payload_invalid", "MFA challenge payload is invalid.");
    public static readonly IdentityRefusal NotForDirectLogin = new("mfa_challenge_not_for_direct_login", "MFA challenge is not valid for direct authentication.");
    public static readonly IdentityRefusal NotForAuthorization = new("mfa_challenge_not_for_authorization", "MFA challenge is not valid for hosted authorization.");
    public static readonly IdentityRefusal EnrollmentPending = new("mfa_enrollment_pending", "MFA enrollment must be completed with its challenge-bound enrollment proof.");
    public static readonly IdentityRefusal CodeInvalid = new("mfa_code_invalid", SqlOSAuthService.MfaChallengeFailureMessage);
    public static readonly IdentityRefusal EnrollmentNotAuthorized = new("mfa_enrollment_not_authorized", "MFA enrollment is not authorized for this challenge.");
    public static readonly IdentityRefusal AuthorizationRequestInvalid = new("authorization_request_invalid", "Authorization request is invalid or expired.");

    /// <summary>
    /// The methods a login proved once its challenge was answered with <paramref name="secondFactor"/>:
    /// the first factor's, as the challenge recorded them, then the second factor, named once
    /// (7.x's <c>SqlOSMfaPolicyService.AddAuthenticationMethod</c>).
    /// </summary>
    public static IReadOnlyList<string> Methods(string firstFactor, string secondFactor)
    {
        var methods = SqlOSMfaPolicyService.SplitAuthenticationMethods(firstFactor)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!methods.Contains(secondFactor, StringComparer.OrdinalIgnoreCase))
        {
            methods.Add(secondFactor);
        }

        return methods;
    }

    /// <summary>
    /// A challenge of the client flow finishes as a direct login, so it is first-party only: a
    /// third-party client that holds one (issued before the direct-login gate, #419) is refused
    /// before its factor is checked or any enrollment state is written. Challenges of the
    /// authorization flow pass, and so does a challenge whose client is gone.
    /// </summary>
    public static async Task EnsureClientFlowIsFirstPartyAsync(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService admin,
        SqlOSTemporaryToken? challenge,
        SqlOSRequestContext request,
        CancellationToken cancellationToken)
    {
        if (challenge == null
            || !string.Equals(challenge.ReadPayload(SqlOSTemporaryTokenKinds.MfaChallenge)?.Flow, Client, StringComparison.Ordinal)
            || challenge.ClientApplicationId == null)
        {
            return;
        }

        var client = await context.Set<SqlOSClientApplication>()
            .FirstOrDefaultAsync(x => x.Id == challenge.ClientApplicationId, cancellationToken);
        if (client != null)
        {
            await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(admin, client, request, challenge.UserId, cancellationToken);
        }
    }

    /// <summary>
    /// Where a login continues once its challenge is answered: the authorization request the
    /// challenge was issued for, or the client's direct login, in the challenge's organization.
    /// </summary>
    public static LoginDestination ContinuationOf(SqlOSTemporaryToken challenge, SqlOSMfaChallengePayload payload)
        => string.Equals(payload.Flow, Client, StringComparison.Ordinal)
            ? new LoginDestination.DirectLoginAfterMfa(challenge.ClientApplicationId!, challenge.OrganizationId, payload.Resource)
            : new LoginDestination.AuthorizationRequestAfterMfa(payload.AuthorizationRequestId!, challenge.OrganizationId, payload.CredentialSignIn);
}

/// <summary>Which login an MFA answer is accepted for: each surface answers the challenges of its own flow.</summary>
internal enum MfaChallengeTarget
{
    /// <summary>The public API: challenges of a first-party client's direct login.</summary>
    DirectLogin = 1,

    /// <summary>The hosted AuthPage and the headless API: challenges of the authorization request they were issued for.</summary>
    AuthorizationRequest = 2
}

/// <summary>The active authorization requests identity processes complete logins for.</summary>
internal static class ActiveAuthorizationRequests
{
    /// <summary>The tracked request <paramref name="authorizationRequestId"/> while it is neither cancelled, completed nor expired at <paramref name="now"/>.</summary>
    public static Task<SqlOSAuthorizationRequest?> FindAsync(
        ISqlOSAuthServerDbContext context,
        string? authorizationRequestId,
        DateTime now,
        CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(authorizationRequestId)
            ? Task.FromResult<SqlOSAuthorizationRequest?>(null)
            : context.Set<SqlOSAuthorizationRequest>()
                .Include(x => x.ClientApplication)
                .FirstOrDefaultAsync(
                    x => x.Id == authorizationRequestId
                        && x.CancelledAt == null
                        && x.CompletedAt == null
                        && x.ExpiresAt > now,
                    cancellationToken);
}
