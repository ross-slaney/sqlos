using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Signs a person in with an address and a password: the hosted AuthPage, the headless API and the
/// public API all run this one implementation, then complete through the hub
/// (<see cref="ILoginCompletion"/>).
/// </summary>
/// <remarks>
/// <para>
/// The comparison is admitted first (<see cref="IAdmissionGate"/>): a locked address, IP address or
/// client is refused before the password is compared, and every comparison is settled, so failures
/// lock the buckets they exhaust. The password is always compared, against a dummy hash when the
/// address has no password, so the answer takes the same time whether or not an account exists, and
/// every failure answers the same public message.
/// </para>
/// <para>
/// A direct login (the public API) is first-party only: the client is refused before anything else.
/// An inactive account is refused at the credential, except by a direct login, which refuses it when
/// the hub issues tokens (7.2.1 behavior, kept).
/// </para>
/// </remarks>
internal sealed class SignInWithPassword(
    ISqlOSAuthServerDbContext context,
    SqlOSSettingsService settings,
    SqlOSAdminService admin,
    IAdmissionGate admission,
    ILoginCompletion completion,
    SqlOSAuthServerOptions options,
    TimeProvider clock)
{
    public async Task<SignInOutcome> ExecuteAsync(SignInWithPasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var credentialSettings = await settings.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!credentialSettings.PasswordEnabled)
        {
            return new SignInOutcome.Refused(IdentityRefusals.PasswordLoginDisabled);
        }

        LoginDestination? directLogin = null;
        if (command.Target is LoginTarget.DirectLogin direct)
        {
            // A direct login returns tokens with no consent screen: a third-party client is refused
            // before its UI can have collected a password (#419).
            var client = await admin.RequireClientAsync(direct.ClientId, cancellationToken);
            await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(admin, client, command.Request, userId: null, cancellationToken);
            directLogin = new LoginDestination.DirectLogin(client, direct.OrganizationId);
        }

        var attempt = admission.BeginPasswordAttempt(
            SqlOSAdminService.NormalizeEmail(command.Email),
            AdmissionOrigin.Of(command.Request),
            command.Attempt.ClientKey,
            command.Attempt.AuthorizationRequestId,
            command.Attempt.Surface);

        var email = await context.Set<SqlOSUserEmail>()
            .AsNoTracking()
            .FindByEmailAsync(command.Email, cancellationToken);
        attempt = attempt with { UserId = email?.UserId };

        if (email != null
            && options.RequireVerifiedEmailForPasswordLogin
            && !email.IsVerified
            && !command.CarriesInvitation)
        {
            return new SignInOutcome.Refused(IdentityRefusals.EmailNotVerified);
        }

        var credential = email == null
            ? null
            : await context.Set<SqlOSCredential>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserId == email.UserId && x.Type == "password" && x.RevokedAt == null, cancellationToken);
        await admission.AdmitPasswordAttemptAsync(attempt, cancellationToken);
        var passwordMatches = HashedSecret
            .FromStored(HashedSecretScheme.Pbkdf2, credential?.SecretHash ?? SqlOSClientAuthenticationService.DummyCredentialHash)
            .Matches(command.Password);
        if (credential == null || !passwordMatches)
        {
            var failureReason = email == null
                ? "unknown_email"
                : credential == null
                    ? "missing_password_credential"
                    : "invalid_password";
            await admission.RecordPasswordAttemptFailedAsync(attempt, failureReason, cancellationToken);
            return new SignInOutcome.Refused(IdentityRefusals.InvalidCredentials);
        }

        var user = await context.Set<SqlOSUser>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == email!.UserId, cancellationToken);
        if (user == null || (!user.IsActive && directLogin == null))
        {
            await admission.RecordPasswordAttemptFailedAsync(attempt, "inactive_user", cancellationToken);
            return new SignInOutcome.Refused(IdentityRefusals.InvalidCredentials);
        }

        await admission.RecordPasswordAttemptSucceededAsync(attempt, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var account = await context.GetUserAsync(user.Id, SqlOSUserParts.Credentials, cancellationToken);
        account.RecordPasswordSignIn(credential.Id, now);
        await context.SaveChangesAsync(cancellationToken);

        var evidence = new LoginEvidence(account, [AuthenticationMethods.Password], [], now);
        var destination = directLogin ?? command.Target switch
        {
            LoginTarget.AuthorizationRequest request => new LoginDestination.AuthorizationRequest(request.Request),
            LoginTarget.Browser browser => new LoginDestination.Browser(browser.InvitationToken, InSignupTransaction: false),
            _ => null
        };
        return new SignInOutcome.SignedIn(
            evidence,
            destination == null
                ? LoginCompletion.NotCompleted.Instance
                : await completion.CompleteAsync(evidence, destination, cancellationToken));
    }
}

/// <summary>A password sign-in.</summary>
/// <param name="Email">The address to sign in, after the surface applied the invitation it carries.</param>
/// <param name="Password">The presented password.</param>
/// <param name="Target">Where the sign-in completes.</param>
/// <param name="CarriesInvitation">
/// The sign-in accepts an invitation, which proves the invited address: an unverified address may
/// then sign in even when <see cref="SqlOSAuthServerOptions.RequireVerifiedEmailForPasswordLogin"/> is set.
/// </param>
/// <param name="Attempt">How the admission buckets and the attempt's audit name the request.</param>
/// <param name="Request">Where the request came from.</param>
internal sealed record SignInWithPasswordCommand(
    string Email,
    string Password,
    LoginTarget Target,
    bool CarriesInvitation,
    PasswordAttemptContext Attempt,
    SqlOSRequestContext Request);

/// <summary>
/// How the password-login admission names an attempt (<c>password.login.*</c> rows): the client key,
/// the authorization request and the surface tag 7.x recorded.
/// </summary>
internal sealed record PasswordAttemptContext(string? ClientKey, string? AuthorizationRequestId, string Surface)
{
    /// <summary>The attempt of a hosted or headless sign-in for <paramref name="request"/>.</summary>
    public static PasswordAttemptContext ForAuthorizationRequest(SqlOSAuthorizationRequest? request, string surface)
        => new(request?.ClientApplication?.ClientId ?? request?.ClientApplicationId, request?.Id, surface);
}
