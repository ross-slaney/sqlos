using System.Text.Json.Nodes;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Creates the account an invitation was sent for, without a password, and signs it in: the hosted
/// AuthPage, the headless API and the public API all sign up from an invitation here.
/// </summary>
/// <remarks>
/// <para>
/// The invitation was mailed to its address and is presented now, which proves the mailbox: the
/// account is created with the address verified. Accepting the invitation into a membership is the
/// invitation's own step (layer 4): the hub accepts the invitation bound to an authorization
/// request or carried by the browser, and a direct login accepts it before its tokens are issued.
/// </para>
/// <para>
/// The sign-up runs in one transaction (<see cref="SignupUnitOfWork"/>). A direct login is
/// first-party only (#419).
/// </para>
/// </remarks>
internal sealed class SignUpWithInvitation(
    ISqlOSAuthServerDbContext context,
    SqlOSSettingsService settings,
    SqlOSAdminService admin,
    SqlOSInvitationService? invitations,
    IHostSignupHook hook,
    ILoginCompletion completion,
    TimeProvider clock)
{
    public async Task<SignUpOutcome> ExecuteAsync(SignUpWithInvitationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        SqlOSClientApplication? client = null;
        if (command.Target is LoginTarget.DirectLogin directLogin)
        {
            // Refused before the transaction opens: the refusal's audit is never rolled back.
            client = await admin.RequireClientAsync(directLogin.ClientId, cancellationToken);
            await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(admin, client, command.Request, userId: null, cancellationToken);
        }

        var conventions = SignupConventions.For(command.Request.Surface, AuthenticationMethods.Invitation);
        var now = clock.GetUtcNow().UtcDateTime;
        var unitOfWork = new SignupUnitOfWork(context);
        return await unitOfWork.RunAsync(
            async () =>
            {
                if (conventions.InvitationSignupNeedsEmailCodes
                    && !(await settings.GetResolvedCredentialSettingsAsync(cancellationToken)).EmailOtpEnabled)
                {
                    return new SignUpOutcome.Refused(IdentityRefusals.InvitationSignupNeedsEmailCodes);
                }

                var invitation = command.Invitation
                    ?? await RequireInvitations().ResolveForSignupAsync(command.InvitationToken, cancellationToken);
                var proof = EmailAddress.TryParse(invitation.Email, out var invitedAddress)
                    ? new OwnershipProof(invitedAddress, OwnershipProofMethod.Invitation)
                    : null;
                var registration = await AccountRegistration.RegisterWithInvitationAsync(
                    context,
                    command.DisplayName!,
                    invitation.Email,
                    proof,
                    now,
                    cancellationToken);
                unitOfWork.Registered(registration.User, registration.CreatedOrganizationId);

                var signup = new SignupCompletion(context, hook, completion, conventions);
                await signup.RunHostHookAsync(
                    registration.User,
                    (command.Target as LoginTarget.AuthorizationRequest)?.Request,
                    invitation.OrganizationId,
                    command.CustomFields ?? invitation.CustomFields,
                    cancellationToken);

                var organizationId = invitation.OrganizationId;
                if (client != null)
                {
                    // A direct login has no hub step that accepts the invitation: it is accepted
                    // here, inside the sign-up, before the tokens are issued.
                    var acceptance = await RequireInvitations().AcceptInCurrentTransactionAsync(
                        command.InvitationToken!,
                        registration.User.Id,
                        AuthenticationMethods.Invitation,
                        command.Request.IpAddress,
                        cancellationToken);
                    organizationId = acceptance.OrganizationId;
                }

                var outcome = await signup.CompleteAsync(
                    new LoginEvidence(registration.User, [AuthenticationMethods.Invitation], proof is null ? [] : [proof], now),
                    SignupDestinations.For(command.Target, client, organizationId),
                    new SignupRecord(AuthenticationMethods.Invitation, organizationId, command.Request.IpAddress, now),
                    cancellationToken);
                if (outcome is SignUpOutcome.SignedUp { Completion: LoginCompletion.DirectLoginCompleted completed } signedUp)
                {
                    // The direct login's answer lists the account's organizations as they stand once
                    // the invitation's membership exists (7.2.1).
                    var organizations = await admin.GetUserOrganizationsAsync(registration.User.Id, cancellationToken);
                    return signedUp with
                    {
                        Completion = new LoginCompletion.DirectLoginCompleted(completed.Result with { Organizations = organizations })
                    };
                }

                return outcome;
            },
            retriable: false,
            cancellationToken);
    }

    private SqlOSInvitationService RequireInvitations()
        => invitations ?? throw new InvalidOperationException("SqlOS invitations are not configured.");
}

/// <summary>An invitation sign-up.</summary>
/// <param name="DisplayName">The name the account will have.</param>
/// <param name="InvitationToken">The invitation's token: the hosted AuthPage's own sign-in and a direct login accept it.</param>
/// <param name="Invitation">
/// The invitation the hosted or headless surface resolved or bound for the request; a direct login
/// resolves it from <see cref="InvitationToken"/>.
/// </param>
/// <param name="CustomFields">The fields the host's sign-up hook receives, before the invitation's own.</param>
/// <param name="Target">Where the first login completes.</param>
/// <param name="Request">Where the request came from.</param>
internal sealed record SignUpWithInvitationCommand(
    string? DisplayName,
    string? InvitationToken,
    SqlOSEmailInvitationResult? Invitation,
    JsonObject? CustomFields,
    LoginTarget Target,
    SqlOSRequestContext Request);
