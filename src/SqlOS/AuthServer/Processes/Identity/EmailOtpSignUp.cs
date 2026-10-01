using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Starts an email-code sign-up on any surface: sends a sign-up code to the typed address and
/// issues the sign-up token that carries the account to create.
/// </summary>
/// <remarks>
/// <para>
/// The answer is the same whether or not the address has an account (the code is sent either way),
/// and an address with an account is audited (<c>email_otp.signup_existing_email</c>); its code is
/// refused at verification, which never creates a second account for a mailbox.
/// </para>
/// <para>
/// A sign-up joins no existing organization unless an invitation names it. A direct login is
/// first-party only (#419). An authorization request remembers the address as its login hint.
/// </para>
/// </remarks>
internal sealed class StartEmailOtpSignUp(
    ISqlOSAuthServerDbContext context,
    SqlOSSettingsService settings,
    SqlOSAdminService admin,
    SqlOSEmailOtpService codes,
    TimeProvider clock)
{
    public async Task<EmailCodeSignUpStartOutcome> ExecuteAsync(StartEmailOtpSignUpCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!(await settings.GetResolvedCredentialSettingsAsync(cancellationToken)).EmailOtpEnabled)
        {
            return new EmailCodeSignUpStartOutcome.Refused(IdentityRefusals.EmailCodesUnavailable);
        }

        var authorizationRequest = (command.Target as LoginTarget.AuthorizationRequest)?.Request;
        SqlOSClientApplication? client = null;
        if (command.Target is LoginTarget.DirectLogin directLogin)
        {
            client = await admin.RequireClientAsync(directLogin.ClientId, cancellationToken);
            await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(admin, client, command.Request, userId: null, cancellationToken);
        }

        var displayName = command.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return new EmailCodeSignUpStartOutcome.Refused(SignupRefusals.DisplayNameRequired);
        }

        if (EmailCodeChallenges.ParseAddress(command.Email) is not { } address)
        {
            return new EmailCodeSignUpStartOutcome.Refused(EmailCodeChallenges.InvalidAddress(command.Email));
        }

        var email = address.Address;
        var clientApplicationId = client?.Id ?? authorizationRequest?.ClientApplicationId;
        var requestedOrganizationId = (command.Target as LoginTarget.DirectLogin)?.OrganizationId ?? authorizationRequest?.OrganizationId;
        var existingEmail = await context.Set<SqlOSUserEmail>()
            .AsNoTracking()
            .FindByEmailAsync(email, cancellationToken);
        if (existingEmail != null)
        {
            codes.AuditRecorder.Record(new EmailOtpSignupStartedForExistingEmail(
                Masked.Email(email),
                command.Request.IpAddress,
                clientApplicationId,
                authorizationRequest?.Id,
                requestedOrganizationId));
            await context.SaveChangesAsync(cancellationToken);
        }

        // An invitation authorizes the join it names; without one, the sign-up may not join.
        var joinsWithoutInvitation = string.IsNullOrWhiteSpace(authorizationRequest?.InvitationId);
        if (joinsWithoutInvitation && SignupRefusals.ForOrganizationJoin(requestedOrganizationId) is { } joinRefusal)
        {
            return new EmailCodeSignUpStartOutcome.Refused(joinRefusal);
        }

        if (authorizationRequest != null)
        {
            authorizationRequest.LoginHintEmail = email;
            await context.SaveChangesAsync(cancellationToken);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var issue = await new EmailCodeChallenges(context, codes).IssueAsync(
            new EmailCodeIssueRequest(
                email,
                EmailOtpPurposes.Signup,
                new EmailOtpChallengeContext(authorizationRequest?.Id, clientApplicationId, RequestedOrganizationId: null),
                SendWhenNoAccount: true,
                command.Request),
            now,
            cancellationToken);
        if (issue is not EmailCodeIssue.Issued issued)
        {
            return new EmailCodeSignUpStartOutcome.Refused(((EmailCodeIssue.Refused)issue).Refusal);
        }

        var challenge = issued.Result;
        var signupToken = SqlOSTemporaryToken.Issue(
            SqlOSTemporaryTokenKinds.EmailOtpSignup,
            new EmailOtpSignupPayload(
                HashedSecret.Sha256(challenge.ChallengeToken).Hash,
                authorizationRequest?.Id,
                client?.ClientId ?? authorizationRequest?.ClientApplication?.ClientId,
                clientApplicationId,
                displayName,
                email,
                string.IsNullOrWhiteSpace(command.OrganizationName) ? null : command.OrganizationName.Trim(),
                OrganizationId: null,
                command.CustomFields),
            new TemporaryTokenBinding(ClientApplicationId: clientApplicationId),
            SqlOSTemporaryTokenKinds.EmailOtpSignup.Lifetime.Resolve(codes.Options.ChallengeLifetime),
            now);
        context.Set<SqlOSTemporaryToken>().Add(signupToken.Token);
        await context.SaveChangesAsync(cancellationToken);

        return new EmailCodeSignUpStartOutcome.Sent(new SqlOSEmailOtpSignupStartResult(
            challenge.ChallengeToken,
            signupToken.RawToken,
            challenge.Email,
            challenge.MaskedEmail,
            challenge.Message,
            challenge.ExpiresAt,
            challenge.NextAllowedSendAt));
    }
}

/// <summary>An email-code sign-up to start.</summary>
/// <param name="DisplayName">The name the account will have.</param>
/// <param name="Email">The address the code is sent to and the account will own.</param>
/// <param name="OrganizationName">The organization the account will create, or null (a sign-up with an invitation creates none).</param>
/// <param name="CustomFields">The fields the host's sign-up hook receives when the account is created.</param>
/// <param name="Target">The authorization request, the hosted AuthPage's own sign-up, or a client's direct login.</param>
/// <param name="Request">Where the request came from.</param>
internal sealed record StartEmailOtpSignUpCommand(
    string? DisplayName,
    string? Email,
    string? OrganizationName,
    JsonObject? CustomFields,
    LoginTarget Target,
    SqlOSRequestContext Request);

/// <summary>What starting an email-code sign-up did.</summary>
internal abstract record EmailCodeSignUpStartOutcome
{
    private EmailCodeSignUpStartOutcome()
    {
    }

    /// <summary>The sign-up code went out; the sign-up token carries the account to create.</summary>
    public sealed record Sent(SqlOSEmailOtpSignupStartResult Result) : EmailCodeSignUpStartOutcome;

    public sealed record Refused(IdentityRefusal Refusal) : EmailCodeSignUpStartOutcome;
}

/// <summary>
/// Verifies an email-code sign-up: its sign-up token, and the code against the challenge the token
/// names. The step the email-code sign-up runs inside its transaction, and what
/// <see cref="SqlOSEmailOtpService.VerifySignupAsync"/> returns.
/// </summary>
internal static class EmailOtpSignupTokens
{
    public static async Task<EmailOtpSignupCheck> VerifyAsync(
        ISqlOSAuthServerDbContext context,
        SqlOSEmailOtpService codes,
        string? signupToken,
        string? challengeToken,
        string? code,
        ChallengeBinding binding,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var rawSignupToken = signupToken?.Trim();
        var token = rawSignupToken is null ? null : await FindAsync(context, rawSignupToken, now, cancellationToken);
        var payload = token?.ReadPayload(SqlOSTemporaryTokenKinds.EmailOtpSignup);
        if (token is null
            || payload is null
            || (binding.RequireMatch && !AnswersAuthorizationRequest(payload.AuthorizationRequestId, binding.AuthorizationRequestId)))
        {
            return EmailOtpSignupCheck.Refused.InvalidCode;
        }

        var rawChallengeToken = challengeToken?.Trim();
        if (rawChallengeToken is null
            || !string.Equals(payload.ChallengeTokenHash, HashedSecret.Sha256(rawChallengeToken).Hash, StringComparison.Ordinal))
        {
            return EmailOtpSignupCheck.Refused.InvalidCode;
        }

        var check = await new EmailCodeChallenges(context, codes).VerifyAsync(
            new EmailCodeVerifyRequest(rawChallengeToken, code, binding, SignUp: true),
            now,
            cancellationToken);
        return check switch
        {
            EmailCodeCheck.Verified verified => new EmailOtpSignupCheck.Verified(
                token,
                new SqlOSEmailOtpSignupVerificationResult(
                    rawSignupToken!,
                    token.ClientApplicationId ?? payload.ClientApplicationId,
                    payload.ClientId,
                    payload.DisplayName,
                    payload.Email,
                    payload.OrganizationName,
                    token.OrganizationId ?? payload.OrganizationId,
                    payload.CustomFields),
                verified.Ownership),
            EmailCodeCheck.Refused refused => new EmailOtpSignupCheck.Refused(refused.Refusal),
            _ => throw new InvalidOperationException($"Unknown email-code check '{check.GetType().Name}'.")
        };
    }

    /// <summary>The unspent, unexpired sign-up token whose raw value is <paramref name="rawToken"/>.</summary>
    public static Task<SqlOSTemporaryToken?> FindAsync(
        ISqlOSAuthServerDbContext context,
        string rawToken,
        DateTime now,
        CancellationToken cancellationToken)
        => context.Set<SqlOSTemporaryToken>()
            .Where(SqlOSTemporaryToken.OfKind(SqlOSTemporaryTokenKinds.EmailOtpSignup))
            .Where(SqlOSTemporaryToken.Presented(rawToken))
            .Where(SqlOSTemporaryToken.UsableAt(now))
            .FirstOrDefaultAsync(cancellationToken);

    private static bool AnswersAuthorizationRequest(string? authorizationRequestId, string? expectedAuthorizationRequestId)
        => string.IsNullOrWhiteSpace(expectedAuthorizationRequestId)
            ? string.IsNullOrWhiteSpace(authorizationRequestId)
            : string.Equals(authorizationRequestId, expectedAuthorizationRequestId, StringComparison.Ordinal);
}

/// <summary>What verifying an email-code sign-up did.</summary>
internal abstract record EmailOtpSignupCheck
{
    private EmailOtpSignupCheck()
    {
    }

    /// <summary>
    /// The code was right: <see cref="Token"/> is the (unspent) sign-up token, <see cref="Result"/>
    /// the account it carries, and <see cref="Ownership"/> the proof of the address it signs up.
    /// </summary>
    public sealed record Verified(SqlOSTemporaryToken Token, SqlOSEmailOtpSignupVerificationResult Result, OwnershipProof Ownership) : EmailOtpSignupCheck;

    public sealed record Refused(IdentityRefusal Refusal) : EmailOtpSignupCheck
    {
        public static Refused InvalidCode { get; } = new(IdentityRefusals.InvalidCode);
    }
}

/// <summary>
/// Completes an email-code sign-up on any surface: the code proves the address, the account is
/// created with it verified, and the new account signs in.
/// </summary>
/// <remarks>
/// <para>
/// The sign-up runs in one transaction (<see cref="SignupUnitOfWork"/>), and so does the code's
/// attempt (7.2.1; the 7.2.2 change #449 moves verification before it). A code for an address an
/// account owns never creates a second account. The challenge's own proof verifies the address.
/// </para>
/// <para>
/// A direct login completes for the client the sign-up token was issued to, which must be
/// first-party (#419): the token's client is refused before the transaction opens, so the refusal
/// is audited, and again once the token is verified.
/// </para>
/// </remarks>
internal sealed class CompleteEmailOtpSignUp(
    ISqlOSAuthServerDbContext context,
    SqlOSSettingsService settings,
    SqlOSAdminService admin,
    SqlOSEmailOtpService codes,
    IHostSignupHook hook,
    ILoginCompletion completion,
    TimeProvider clock)
{
    public async Task<SignUpOutcome> ExecuteAsync(CompleteEmailOtpSignUpCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = clock.GetUtcNow().UtcDateTime;
        var directLogin = command.Target is LoginTarget.DirectLogin;
        if (directLogin)
        {
            await SignupTokenClients.EnsureFirstPartyAsync(context, admin, SqlOSTemporaryTokenKinds.EmailOtpSignup, command.SignupToken, command.Request, now, cancellationToken);
        }

        var unitOfWork = new SignupUnitOfWork(context);
        return await unitOfWork.RunAsync(
            async () =>
            {
                if (!(await settings.GetResolvedCredentialSettingsAsync(cancellationToken)).EmailOtpEnabled)
                {
                    return new SignUpOutcome.Refused(IdentityRefusals.EmailCodesUnavailable);
                }

                var check = await EmailOtpSignupTokens.VerifyAsync(
                    context,
                    codes,
                    command.SignupToken,
                    command.ChallengeToken,
                    command.Code,
                    ChallengeBinding.For(command.Target),
                    now,
                    cancellationToken);
                if (check is not EmailOtpSignupCheck.Verified verified)
                {
                    return new SignUpOutcome.Refused(((EmailOtpSignupCheck.Refused)check).Refusal);
                }

                var verification = verified.Result;
                SqlOSClientApplication? client = null;
                if (directLogin)
                {
                    if (string.IsNullOrWhiteSpace(verification.ClientApplicationId))
                    {
                        return new SignUpOutcome.Refused(IdentityRefusals.InvalidCode);
                    }

                    client = await context.Set<SqlOSClientApplication>()
                        .FirstAsync(x => x.Id == verification.ClientApplicationId, cancellationToken);
                    await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(admin, client, command.Request, userId: null, cancellationToken);
                }

                var authorizationRequest = (command.Target as LoginTarget.AuthorizationRequest)?.Request;
                var invitation = command.Invitation;
                var registered = await AccountRegistration.RegisterWithEmailOtpAsync(
                    context,
                    admin,
                    settings,
                    verification.DisplayName,
                    verification.Email,
                    verified.Ownership,
                    invitation == null ? verification.OrganizationName : null,
                    invitation == null ? authorizationRequest?.OrganizationId ?? verification.OrganizationId : null,
                    now,
                    cancellationToken);
                if (registered is not SignupRegistrationOutcome.Registered { Registration: var registration })
                {
                    return new SignUpOutcome.Refused(((SignupRegistrationOutcome.Refused)registered).Refusal);
                }

                unitOfWork.Registered(registration.User, registration.CreatedOrganizationId);
                var organizationId = invitation?.OrganizationId ?? registration.Organizations.FirstOrDefault()?.Id;
                var signup = new SignupCompletion(
                    context,
                    hook,
                    completion,
                    SignupConventions.For(command.Request.Surface, AuthenticationMethods.EmailOtp));
                await signup.RunHostHookAsync(
                    registration.User,
                    authorizationRequest,
                    organizationId,
                    verification.CustomFields ?? invitation?.CustomFields,
                    cancellationToken);
                var outcome = await signup.CompleteAsync(
                    new LoginEvidence(registration.User, [AuthenticationMethods.EmailOtp], [verified.Ownership], now),
                    SignupDestinations.For(command.Target, client, organizationId),
                    new SignupRecord(
                        AuthenticationMethods.EmailOtp,
                        organizationId,
                        command.Request.IpAddress,
                        now,
                        verified.Token,
                        SqlOSTemporaryTokenKinds.EmailOtpSignup),
                    cancellationToken);
                return outcome is SignUpOutcome.SignedUp signedUp ? signedUp with { Email = verification.Email } : outcome;
            },
            retriable: false,
            cancellationToken);
    }
}

/// <summary>An email-code sign-up's code, presented with its sign-up token.</summary>
/// <param name="SignupToken">The token the sign-up's start issued.</param>
/// <param name="ChallengeToken">The challenge the code answers.</param>
/// <param name="Code">The code the person typed.</param>
/// <param name="Invitation">The invitation the surface resolved or bound for the request: its organization is the sign-up's, and it creates none of its own.</param>
/// <param name="Target">Where the first login completes; a direct login completes for the token's client.</param>
/// <param name="Request">Where the request came from.</param>
internal sealed record CompleteEmailOtpSignUpCommand(
    string? SignupToken,
    string? ChallengeToken,
    string? Code,
    SqlOSEmailInvitationResult? Invitation,
    LoginTarget Target,
    SqlOSRequestContext Request);

/// <summary>
/// The first-party check of the client a sign-up token was issued to, made before a direct-login
/// sign-up opens its transaction so a refusal's audit is never rolled back (#419).
/// </summary>
internal static class SignupTokenClients
{
    public static async Task EnsureFirstPartyAsync(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService admin,
        TemporaryTokenKind kind,
        string? signupToken,
        SqlOSRequestContext request,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(signupToken))
        {
            return;
        }

        var rawToken = signupToken.Trim();
        var token = await context.Set<SqlOSTemporaryToken>()
            .Where(SqlOSTemporaryToken.OfKind(kind))
            .Where(SqlOSTemporaryToken.Presented(rawToken))
            .Where(SqlOSTemporaryToken.UsableAt(now))
            .FirstOrDefaultAsync(cancellationToken);
        if (token?.ClientApplicationId == null)
        {
            return;
        }

        var client = await context.Set<SqlOSClientApplication>()
            .FirstOrDefaultAsync(x => x.Id == token.ClientApplicationId, cancellationToken);
        if (client != null)
        {
            await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(admin, client, request, token.UserId, cancellationToken);
        }
    }
}
