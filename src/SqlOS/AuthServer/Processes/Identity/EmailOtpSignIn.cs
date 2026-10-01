using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Sends a sign-in code to an address: the hosted AuthPage, the headless API and the public API
/// all start an email-code sign-in here.
/// </summary>
/// <remarks>
/// The answer never reveals whether an account exists: a code is issued for every valid address,
/// but sent only when an active account owns it, and only to the address stored on that account.
/// A direct login is first-party only, so a third-party client is refused before a code is issued
/// (#419). An authorization request remembers the address as its login hint.
/// </remarks>
internal sealed class StartEmailOtpSignIn(
    ISqlOSAuthServerDbContext context,
    SqlOSSettingsService settings,
    SqlOSAdminService admin,
    SqlOSEmailOtpService codes,
    TimeProvider clock)
{
    public async Task<EmailCodeStartOutcome> ExecuteAsync(StartEmailOtpSignInCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.RequiresInvitedAccount)
        {
            // An invitation is accepted by signing in to the invited account: an address without
            // one signs up instead, and an inactive one is refused (the headless surface).
            var invited = await context.Set<SqlOSUserEmail>()
                .Include(x => x.User)
                .AsNoTracking()
                .FindByEmailAsync(command.Email, cancellationToken);
            if (invited == null)
            {
                return new EmailCodeStartOutcome.Refused(IdentityRefusals.InvitedAccountMissing);
            }

            if (invited.User?.IsActive != true)
            {
                return new EmailCodeStartOutcome.Refused(IdentityRefusals.InvitedAccountInactive);
            }
        }

        if (!(await settings.GetResolvedCredentialSettingsAsync(cancellationToken)).EmailOtpEnabled)
        {
            return new EmailCodeStartOutcome.Refused(IdentityRefusals.EmailCodesUnavailable);
        }

        EmailOtpChallengeContext challengeContext;
        switch (command.Target)
        {
            case LoginTarget.AuthorizationRequest request:
                request.Request.LoginHintEmail = command.Email.Trim();
                await context.SaveChangesAsync(cancellationToken);
                challengeContext = new EmailOtpChallengeContext(request.Request.Id, request.Request.ClientApplicationId, RequestedOrganizationId: null);
                break;
            case LoginTarget.DirectLogin directLogin:
                var client = await admin.RequireClientAsync(directLogin.ClientId, cancellationToken);
                await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(admin, client, command.Request, userId: null, cancellationToken);
                challengeContext = new EmailOtpChallengeContext(AuthorizationRequestId: null, client.Id, directLogin.OrganizationId);
                break;
            default:
                challengeContext = new EmailOtpChallengeContext(null, null, null);
                break;
        }

        var issue = await new EmailCodeChallenges(context, codes).IssueAsync(
            new EmailCodeIssueRequest(command.Email, EmailOtpPurposes.Login, challengeContext, SendWhenNoAccount: false, command.Request),
            clock.GetUtcNow().UtcDateTime,
            cancellationToken);
        return issue switch
        {
            EmailCodeIssue.Issued issued => new EmailCodeStartOutcome.Sent(issued.Result),
            EmailCodeIssue.Refused refused => new EmailCodeStartOutcome.Refused(refused.Refusal),
            _ => throw new InvalidOperationException($"Unknown email-code issue '{issue.GetType().Name}'.")
        };
    }
}

/// <summary>An email-code sign-in to start.</summary>
/// <param name="Email">The address, after the surface applied the invitation it carries.</param>
/// <param name="Target">Where the sign-in will complete: it binds the code to the authorization request or the client.</param>
/// <param name="Request">Where the request came from.</param>
/// <param name="RequiresInvitedAccount">The sign-in accepts an invitation and needs an active account for the address (headless).</param>
internal sealed record StartEmailOtpSignInCommand(
    string Email,
    LoginTarget Target,
    SqlOSRequestContext Request,
    bool RequiresInvitedAccount = false);

/// <summary>What starting an email-code sign-in or sign-up did.</summary>
internal abstract record EmailCodeStartOutcome
{
    private EmailCodeStartOutcome()
    {
    }

    /// <summary>The code was issued (and sent when an account owns the address, or for a sign-up).</summary>
    public sealed record Sent(SqlOSEmailOtpStartResult Result) : EmailCodeStartOutcome;

    /// <summary>No code went out.</summary>
    public sealed record Refused(IdentityRefusal Refusal) : EmailCodeStartOutcome;
}

/// <summary>
/// Signs a person in with the code sent to their address: one implementation for the hosted
/// AuthPage, the headless API and the public API.
/// </summary>
/// <remarks>
/// The attempt is spent before the code is compared and commits on its own, so verification never
/// runs inside a transaction (#424). A right code proves the mailbox: an unverified address is
/// claimed, evicting what was attached before (#420, #422), and becomes the account's default email.
/// A direct login completes for the client the code was issued to; a code issued to none is refused.
/// </remarks>
internal sealed class VerifyEmailOtpSignIn(
    ISqlOSAuthServerDbContext context,
    SqlOSSettingsService settings,
    SqlOSEmailOtpService codes,
    ILoginCompletion completion,
    TimeProvider clock)
{
    public async Task<EmailCodeSignInOutcome> ExecuteAsync(VerifyEmailOtpSignInCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!(await settings.GetResolvedCredentialSettingsAsync(cancellationToken)).EmailOtpEnabled)
        {
            return new EmailCodeSignInOutcome.Refused(IdentityRefusals.EmailCodesUnavailable);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var check = await new EmailCodeChallenges(context, codes).VerifyAsync(
            new EmailCodeVerifyRequest(command.ChallengeToken, command.Code, command.Binding, SignUp: false),
            now,
            cancellationToken);
        if (check is not EmailCodeCheck.Verified verified)
        {
            return new EmailCodeSignInOutcome.Refused(((EmailCodeCheck.Refused)check).Refusal);
        }

        var challenge = verified.Challenge;
        if (challenge.User is not { IsActive: true } user)
        {
            return new EmailCodeSignInOutcome.Refused(IdentityRefusals.InvalidCode);
        }

        var evidence = new LoginEvidence(user, [AuthenticationMethods.EmailOtp], [verified.Ownership], now);
        LoginDestination? destination;
        switch (command.Target)
        {
            case LoginTarget.AuthorizationRequest request:
                destination = new LoginDestination.AuthorizationRequest(request.Request);
                break;
            case LoginTarget.Browser browser:
                destination = new LoginDestination.Browser(browser.InvitationToken, InSignupTransaction: false);
                break;
            case LoginTarget.DirectLogin:
                // A direct login completes for the client the code was issued to.
                if (challenge.ClientApplicationId == null)
                {
                    return new EmailCodeSignInOutcome.Refused(IdentityRefusals.InvalidCode);
                }

                var client = await context.Set<SqlOSClientApplication>()
                    .FirstAsync(x => x.Id == challenge.ClientApplicationId, cancellationToken);
                destination = new LoginDestination.DirectLogin(client, challenge.RequestedOrganizationId);
                break;
            default:
                destination = null;
                break;
        }

        return new EmailCodeSignInOutcome.SignedIn(
            evidence,
            destination == null
                ? LoginCompletion.NotCompleted.Instance
                : await completion.CompleteAsync(evidence, destination, cancellationToken),
            challenge);
    }
}

/// <summary>A sign-in code presented for its challenge.</summary>
/// <param name="ChallengeToken">The challenge the code answers.</param>
/// <param name="Code">The code the person typed.</param>
/// <param name="Target">Where the login completes; a direct login completes for the challenge's client.</param>
/// <param name="Binding">The authorization request the code must answer (<see cref="ChallengeBinding.For"/> the target).</param>
internal sealed record VerifyEmailOtpSignInCommand(
    string? ChallengeToken,
    string? Code,
    LoginTarget Target,
    ChallengeBinding Binding);

/// <summary>What an email-code sign-in did.</summary>
internal abstract record EmailCodeSignInOutcome
{
    private EmailCodeSignInOutcome()
    {
    }

    /// <summary>The code proved the mailbox, and the hub completed the login.</summary>
    public sealed record SignedIn(LoginEvidence Evidence, LoginCompletion Completion, SqlOSEmailOtpChallenge Challenge) : EmailCodeSignInOutcome;

    /// <summary>The code was refused.</summary>
    public sealed record Refused(IdentityRefusal Refusal) : EmailCodeSignInOutcome;
}
