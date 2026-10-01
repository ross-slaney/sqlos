using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Creates an account with an address and a password and signs it in: the hosted AuthPage, the
/// headless API and the public API all sign up with a password here.
/// </summary>
/// <remarks>
/// <para>
/// Password sign-up must be enabled, the input within bounds and the password under the password
/// policy, and a sign-up joins no existing organization without an invitation. The address is not
/// verified: a later proof of its mailbox claims it (#420). The account, the organization it
/// names, its first login and its record commit together (<see cref="SignupUnitOfWork"/>).
/// </para>
/// <para>
/// A direct login is first-party only, refused before anything is written (#419). When the client
/// then refuses the new account application access, the rolled-back sign-up still audits the
/// denial (7.2.1).
/// </para>
/// </remarks>
internal sealed class SignUpWithPassword(
    ISqlOSAuthServerDbContext context,
    SqlOSSettingsService settings,
    SqlOSAdminService admin,
    IHostSignupHook hook,
    ILoginCompletion completion,
    TimeProvider clock)
{
    public async Task<SignUpOutcome> ExecuteAsync(SignUpWithPasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var check = await AccountRegistration.CheckPasswordSignupAsync(
            settings,
            command.DisplayName,
            command.Email,
            command.Password,
            command.OrganizationName,
            command.JoinOrganizationId,
            cancellationToken);
        if (check is not PasswordSignupCheck.Passed passed)
        {
            return new SignUpOutcome.Refused(((PasswordSignupCheck.Refused)check).Refusal);
        }

        var input = passed.Input;
        SqlOSClientApplication? client = null;
        if (command.Target is LoginTarget.DirectLogin directLogin)
        {
            client = await admin.RequireClientAsync(directLogin.ClientId, cancellationToken);
            await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(admin, client, command.Request, userId: null, cancellationToken);
        }

        var credentialOnly = command.Target is LoginTarget.CredentialOnly;
        var now = clock.GetUtcNow().UtcDateTime;
        var unitOfWork = new SignupUnitOfWork(context);
        string? firstOrganizationId = null;
        try
        {
            return await unitOfWork.RunAsync(
                async () =>
                {
                    var registration = await AccountRegistration.RegisterWithPasswordAsync(context, admin, input, now, cancellationToken);
                    unitOfWork.Registered(registration.User, registration.CreatedOrganizationId);
                    firstOrganizationId = registration.Organizations.FirstOrDefault()?.Id;
                    var evidence = new LoginEvidence(registration.User, [AuthenticationMethods.Password], [], now);
                    if (credentialOnly)
                    {
                        return new SignUpOutcome.SignedUp(evidence, LoginCompletion.NotCompleted.Instance, input.Email);
                    }

                    var organizationId = command.Invitation?.OrganizationId ?? firstOrganizationId;
                    var signup = new SignupCompletion(
                        context,
                        hook,
                        completion,
                        SignupConventions.For(command.Request.Surface, AuthenticationMethods.Password));
                    await signup.RunHostHookAsync(
                        registration.User,
                        (command.Target as LoginTarget.AuthorizationRequest)?.Request,
                        organizationId,
                        command.CustomFields,
                        cancellationToken);
                    return await signup.CompleteAsync(
                        evidence,
                        SignupDestinations.For(command.Target, client, firstOrganizationId),
                        new SignupRecord(AuthenticationMethods.Password, organizationId, command.Request.IpAddress, now),
                        cancellationToken);
                },
                // The public API's password sign-up runs under the provider's execution strategy (7.2.1).
                retriable: client != null || credentialOnly,
                cancellationToken);
        }
        catch (InvalidOperationException exception) when (
            client != null
            && unitOfWork.Account != null
            && exception.Message == SqlOSAdminService.ApplicationAccessDeniedMessage
            && SqlOSSignupOrchestration.SupportsDatabaseTransactions(context))
        {
            await AuditRolledBackAccessDenialAsync(client, unitOfWork.Account.Id, firstOrganizationId, command.Request, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// The rollback took the client's application-access denial with it: it is audited again,
    /// outside the sign-up, for the account that no longer exists (7.2.1).
    /// </summary>
    private async Task AuditRolledBackAccessDenialAsync(
        SqlOSClientApplication client,
        string userId,
        string? organizationId,
        SqlOSRequestContext request,
        CancellationToken cancellationToken)
    {
        if (context is DbContext tracked)
        {
            tracked.ChangeTracker.Clear();
        }

        try
        {
            await admin.EnsureApplicationAccessAsync(
                client,
                userId,
                organizationId,
                "application.access.token_denied",
                request.IpAddress,
                cancellationToken);
        }
        catch (InvalidOperationException denied) when (denied.Message == SqlOSAdminService.ApplicationAccessDeniedMessage)
        {
        }
    }
}

/// <summary>A password sign-up.</summary>
/// <param name="DisplayName">The name the account will have.</param>
/// <param name="Email">The address the account will have, unverified (#420).</param>
/// <param name="Password">The password, checked against the password policy.</param>
/// <param name="OrganizationName">The organization the account creates and owns, or null (a sign-up with an invitation creates none).</param>
/// <param name="JoinOrganizationId">An existing organization the request asks to join, which a sign-up without an invitation is refused.</param>
/// <param name="Invitation">The invitation the surface resolved for the request: its organization is the sign-up's.</param>
/// <param name="CustomFields">The fields the host's sign-up hook receives.</param>
/// <param name="Target">Where the first login completes; <see cref="LoginTarget.CredentialOnly"/> only registers the account.</param>
/// <param name="Request">Where the request came from.</param>
internal sealed record SignUpWithPasswordCommand(
    string? DisplayName,
    string? Email,
    string? Password,
    string? OrganizationName,
    string? JoinOrganizationId,
    SqlOSEmailInvitationResult? Invitation,
    JsonObject? CustomFields,
    LoginTarget Target,
    SqlOSRequestContext Request);

/// <summary>Where a sign-up's first login completes.</summary>
internal static class SignupDestinations
{
    /// <summary>
    /// The destination of <paramref name="target"/>: the authorization request, the hosted
    /// AuthPage's own sign-in (accepting its invitation inside the sign-up's transaction), or the
    /// client's direct login into <paramref name="organizationId"/>.
    /// </summary>
    public static LoginDestination For(LoginTarget target, SqlOSClientApplication? client, string? organizationId) => target switch
    {
        LoginTarget.AuthorizationRequest request => new LoginDestination.AuthorizationRequest(request.Request),
        LoginTarget.Browser browser => new LoginDestination.Browser(browser.InvitationToken, InSignupTransaction: true),
        LoginTarget.DirectLogin => new LoginDestination.DirectLogin(
            client ?? throw new InvalidOperationException("A direct login completes for a client."),
            organizationId),
        _ => throw new InvalidOperationException($"A sign-up does not complete at '{target.GetType().Name}'.")
    };
}
