using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Sends a sign-in code to a phone number: the hosted AuthPage, the headless API and the public API
/// all start a phone-code sign-in here.
/// </summary>
/// <remarks>
/// The answer never reveals whether an account exists: a challenge is issued for every valid
/// number, but the provider sends a code only when an active account owns the number. A direct
/// login is first-party only (#419).
/// </remarks>
internal sealed class StartPhoneOtpSignIn(
    ISqlOSAuthServerDbContext context,
    SqlOSSettingsService settings,
    SqlOSAdminService admin,
    SqlOSPhoneOtpService codes,
    TimeProvider clock)
{
    public async Task<PhoneCodeStartOutcome> ExecuteAsync(StartPhoneOtpSignInCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!(await settings.GetResolvedCredentialSettingsAsync(cancellationToken)).PhoneOtpEnabled)
        {
            return new PhoneCodeStartOutcome.Refused(IdentityRefusals.PhoneCodesUnavailable);
        }

        PhoneOtpChallengeContext challengeContext;
        switch (command.Target)
        {
            case LoginTarget.AuthorizationRequest request:
                challengeContext = new PhoneOtpChallengeContext(request.Request.Id, request.Request.ClientApplicationId, RequestedOrganizationId: null);
                break;
            case LoginTarget.DirectLogin directLogin:
                var client = await admin.RequireClientAsync(directLogin.ClientId, cancellationToken);
                await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(admin, client, command.Request, userId: null, cancellationToken);
                challengeContext = new PhoneOtpChallengeContext(AuthorizationRequestId: null, client.Id, directLogin.OrganizationId);
                break;
            default:
                challengeContext = new PhoneOtpChallengeContext(null, null, null);
                break;
        }

        var issue = await new PhoneCodeChallenges(context, codes).IssueAsync(
            new PhoneCodeIssueRequest(command.PhoneNumber, PhoneOtpPurposes.Login, challengeContext, UserId: null, SendWhenNoAccount: false, command.Request),
            clock.GetUtcNow().UtcDateTime,
            cancellationToken);
        return issue switch
        {
            PhoneCodeIssue.Issued issued => new PhoneCodeStartOutcome.Sent(issued.Result),
            PhoneCodeIssue.Refused refused => new PhoneCodeStartOutcome.Refused(refused.Refusal),
            _ => throw new InvalidOperationException($"Unknown phone-code issue '{issue.GetType().Name}'.")
        };
    }
}

/// <summary>A phone-code sign-in to start.</summary>
/// <param name="PhoneNumber">The number as the request typed it.</param>
/// <param name="Target">Where the sign-in will complete: it binds the code to the authorization request or the client.</param>
/// <param name="Request">Where the request came from.</param>
internal sealed record StartPhoneOtpSignInCommand(string PhoneNumber, LoginTarget Target, SqlOSRequestContext Request);

/// <summary>What starting a phone-code sign-in did.</summary>
internal abstract record PhoneCodeStartOutcome
{
    private PhoneCodeStartOutcome()
    {
    }

    public sealed record Sent(SqlOSPhoneOtpStartResult Result) : PhoneCodeStartOutcome;

    public sealed record Refused(IdentityRefusal Refusal) : PhoneCodeStartOutcome;
}

/// <summary>
/// Signs a person in with the code sent to their phone: one implementation for the hosted AuthPage,
/// the headless API and the public API.
/// </summary>
/// <remarks>
/// The provider checks the code against the challenge's stored number. The account the number
/// belongs to signs in; the number records its use. A direct login completes for the client the
/// code was issued to, and a code issued to none is refused.
/// </remarks>
internal sealed class VerifyPhoneOtpSignIn(
    ISqlOSAuthServerDbContext context,
    SqlOSSettingsService settings,
    SqlOSPhoneOtpService codes,
    ILoginCompletion completion,
    TimeProvider clock)
{
    public async Task<PhoneCodeSignInOutcome> ExecuteAsync(VerifyPhoneOtpSignInCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!(await settings.GetResolvedCredentialSettingsAsync(cancellationToken)).PhoneOtpEnabled)
        {
            return new PhoneCodeSignInOutcome.Refused(IdentityRefusals.PhoneCodesUnavailable);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var check = await new PhoneCodeChallenges(context, codes).VerifyAsync(
            new PhoneCodeVerifyRequest(command.ChallengeToken, command.Code, PhoneOtpPurposes.Login, command.Binding),
            now,
            cancellationToken);
        if (check is not PhoneCodeCheck.Verified verified)
        {
            return new PhoneCodeSignInOutcome.Refused(((PhoneCodeCheck.Refused)check).Refusal);
        }

        var challenge = verified.Challenge;
        if (challenge.User is not { IsActive: true } user)
        {
            return new PhoneCodeSignInOutcome.Refused(IdentityRefusals.InvalidCode);
        }

        if (challenge.UserPhoneNumberId != null)
        {
            await context.LoadUserPartsAsync(user, SqlOSUserParts.PhoneNumbers, cancellationToken);
        }

        user.RecordPhoneSignIn(challenge.UserPhoneNumberId, now);
        await context.SaveChangesAsync(cancellationToken);

        var evidence = new LoginEvidence(user, [AuthenticationMethods.PhoneOtp], [], now);
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
                    return new PhoneCodeSignInOutcome.Refused(IdentityRefusals.InvalidCode);
                }

                var client = await context.Set<SqlOSClientApplication>()
                    .FirstAsync(x => x.Id == challenge.ClientApplicationId, cancellationToken);
                destination = new LoginDestination.DirectLogin(client, challenge.RequestedOrganizationId);
                break;
            default:
                destination = null;
                break;
        }

        return new PhoneCodeSignInOutcome.SignedIn(
            evidence,
            destination == null
                ? LoginCompletion.NotCompleted.Instance
                : await completion.CompleteAsync(evidence, destination, cancellationToken),
            challenge);
    }
}

/// <summary>A sign-in code presented for its phone challenge.</summary>
internal sealed record VerifyPhoneOtpSignInCommand(
    string? ChallengeToken,
    string? Code,
    LoginTarget Target,
    ChallengeBinding Binding);

/// <summary>What a phone-code sign-in did.</summary>
internal abstract record PhoneCodeSignInOutcome
{
    private PhoneCodeSignInOutcome()
    {
    }

    public sealed record SignedIn(LoginEvidence Evidence, LoginCompletion Completion, SqlOSPhoneOtpChallenge Challenge) : PhoneCodeSignInOutcome;

    public sealed record Refused(IdentityRefusal Refusal) : PhoneCodeSignInOutcome;
}

/// <summary>
/// Starts a phone-code sign-up on any surface: sends a sign-up code to the number and issues the
/// sign-up token that carries the account to create.
/// </summary>
/// <remarks>
/// A number one account already has is refused up front. Phone sign-up does not accept an email
/// invitation. A sign-up joins no existing organization. A direct login is first-party only (#419).
/// </remarks>
internal sealed class StartPhoneOtpSignUp(
    ISqlOSAuthServerDbContext context,
    SqlOSSettingsService settings,
    SqlOSAdminService admin,
    SqlOSPhoneOtpService codes,
    TimeProvider clock)
{
    public async Task<PhoneCodeSignUpStartOutcome> ExecuteAsync(StartPhoneOtpSignUpCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CarriesInvitation)
        {
            return new PhoneCodeSignUpStartOutcome.Refused(IdentityRefusals.PhoneSignupWithInvitation);
        }

        if (!(await settings.GetResolvedCredentialSettingsAsync(cancellationToken)).PhoneOtpEnabled)
        {
            return new PhoneCodeSignUpStartOutcome.Refused(IdentityRefusals.PhoneCodesUnavailable);
        }

        var authorizationRequest = (command.Target as LoginTarget.AuthorizationRequest)?.Request;
        var directLogin = command.Target as LoginTarget.DirectLogin;
        SqlOSClientApplication? client = null;
        if (directLogin != null)
        {
            client = await admin.RequireClientAsync(directLogin.ClientId, cancellationToken);
            await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(admin, client, command.Request, userId: null, cancellationToken);
        }

        var displayName = command.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return new PhoneCodeSignUpStartOutcome.Refused(SignupRefusals.DisplayNameRequired);
        }

        var organizationId = directLogin != null ? directLogin.OrganizationId : authorizationRequest?.OrganizationId;
        if (SignupRefusals.ForOrganizationJoin(organizationId) is { } joinRefusal)
        {
            return new PhoneCodeSignUpStartOutcome.Refused(joinRefusal);
        }

        var phoneNumber = codes.Normalize(command.PhoneNumber);
        if (phoneNumber is PhoneNumberCheck.Invalid invalid)
        {
            return new PhoneCodeSignUpStartOutcome.Refused(invalid.Refusal);
        }

        var e164 = ((PhoneNumberCheck.Valid)phoneNumber).PhoneNumber.E164;
        var phoneHash = HashedSecret.Sha256(e164).Hash;
        if (await context.Set<SqlOSUserPhoneNumber>().AsNoTracking().AnyAsync(x => x.PhoneNumberHash == phoneHash && x.RemovedAt == null, cancellationToken))
        {
            return new PhoneCodeSignUpStartOutcome.Refused(IdentityRefusals.PhoneNumberTaken);
        }

        var clientApplicationId = client?.Id ?? authorizationRequest?.ClientApplicationId;
        var now = clock.GetUtcNow().UtcDateTime;
        var issue = await new PhoneCodeChallenges(context, codes).IssueAsync(
            new PhoneCodeIssueRequest(
                e164,
                PhoneOtpPurposes.Signup,
                new PhoneOtpChallengeContext(authorizationRequest?.Id, clientApplicationId, RequestedOrganizationId: null),
                UserId: null,
                SendWhenNoAccount: true,
                command.Request),
            now,
            cancellationToken);
        if (issue is not PhoneCodeIssue.Issued issued)
        {
            return new PhoneCodeSignUpStartOutcome.Refused(((PhoneCodeIssue.Refused)issue).Refusal);
        }

        var challenge = issued.Result;
        var signupToken = SqlOSTemporaryToken.Issue(
            SqlOSTemporaryTokenKinds.PhoneOtpSignup,
            new PhoneOtpSignupPayload(
                HashedSecret.Sha256(challenge.ChallengeToken).Hash,
                authorizationRequest?.Id,
                client?.ClientId ?? authorizationRequest?.ClientApplication?.ClientId,
                clientApplicationId,
                displayName,
                challenge.PhoneNumber,
                string.IsNullOrWhiteSpace(command.OrganizationName) ? null : command.OrganizationName.Trim(),
                organizationId,
                command.CustomFields),
            new TemporaryTokenBinding(ClientApplicationId: clientApplicationId, OrganizationId: directLogin?.OrganizationId),
            SqlOSTemporaryTokenKinds.PhoneOtpSignup.Lifetime.Resolve(codes.Options.ChallengeLifetime),
            now);
        context.Set<SqlOSTemporaryToken>().Add(signupToken.Token);
        await context.SaveChangesAsync(cancellationToken);

        return new PhoneCodeSignUpStartOutcome.Sent(new SqlOSPhoneOtpSignupStartResult(
            challenge.ChallengeToken,
            signupToken.RawToken,
            challenge.PhoneNumber,
            challenge.MaskedPhoneNumber,
            challenge.Message,
            challenge.ExpiresAt,
            challenge.NextAllowedSendAt));
    }
}

/// <summary>A phone-code sign-up to start.</summary>
/// <param name="DisplayName">The name the account will have.</param>
/// <param name="PhoneNumber">The number as the request typed it: the code is sent to it and the account will own it.</param>
/// <param name="OrganizationName">The organization the account will create, or null.</param>
/// <param name="CustomFields">The fields the host's sign-up hook receives when the account is created.</param>
/// <param name="Target">The authorization request, the hosted AuthPage's own sign-up, or a client's direct login.</param>
/// <param name="CarriesInvitation">The surface resolved an email invitation, which a phone sign-up cannot accept.</param>
/// <param name="Request">Where the request came from.</param>
internal sealed record StartPhoneOtpSignUpCommand(
    string? DisplayName,
    string PhoneNumber,
    string? OrganizationName,
    JsonObject? CustomFields,
    LoginTarget Target,
    bool CarriesInvitation,
    SqlOSRequestContext Request);

/// <summary>What starting a phone-code sign-up did.</summary>
internal abstract record PhoneCodeSignUpStartOutcome
{
    private PhoneCodeSignUpStartOutcome()
    {
    }

    public sealed record Sent(SqlOSPhoneOtpSignupStartResult Result) : PhoneCodeSignUpStartOutcome;

    public sealed record Refused(IdentityRefusal Refusal) : PhoneCodeSignUpStartOutcome;
}

/// <summary>
/// Verifies a phone-code sign-up: its sign-up token, and the code against the challenge the token
/// names. The step the phone-code sign-up runs inside its transaction, and what
/// <see cref="SqlOSPhoneOtpService.VerifySignupAsync"/> returns.
/// </summary>
internal static class PhoneOtpSignupTokens
{
    public static async Task<PhoneOtpSignupCheck> VerifyAsync(
        ISqlOSAuthServerDbContext context,
        SqlOSPhoneOtpService codes,
        string? signupToken,
        string? challengeToken,
        string? code,
        ChallengeBinding binding,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var rawSignupToken = signupToken?.Trim();
        var token = rawSignupToken is null ? null : await FindAsync(context, rawSignupToken, now, cancellationToken);
        var payload = token?.ReadPayload(SqlOSTemporaryTokenKinds.PhoneOtpSignup);
        if (token is null || payload is null || !Answers(payload, binding))
        {
            return PhoneOtpSignupCheck.Refused.InvalidCode;
        }

        var rawChallengeToken = challengeToken?.Trim();
        if (rawChallengeToken is null
            || !string.Equals(payload.ChallengeTokenHash, HashedSecret.Sha256(rawChallengeToken).Hash, StringComparison.Ordinal))
        {
            return PhoneOtpSignupCheck.Refused.InvalidCode;
        }

        var check = await new PhoneCodeChallenges(context, codes).VerifyAsync(
            new PhoneCodeVerifyRequest(rawChallengeToken, code, PhoneOtpPurposes.Signup, binding),
            now,
            cancellationToken);
        if (check is not PhoneCodeCheck.Verified verified)
        {
            return new PhoneOtpSignupCheck.Refused(((PhoneCodeCheck.Refused)check).Refusal);
        }

        // A sign-up code never creates a second account for a number one account has.
        var phoneHash = verified.Challenge.PhoneNumberHash;
        if (verified.Challenge.User != null
            || await context.Set<SqlOSUserPhoneNumber>().AsNoTracking().AnyAsync(x => x.PhoneNumberHash == phoneHash && x.RemovedAt == null, cancellationToken))
        {
            return new PhoneOtpSignupCheck.Refused(IdentityRefusals.PhoneNumberTaken);
        }

        return new PhoneOtpSignupCheck.Verified(
            token,
            new SqlOSPhoneOtpSignupVerificationResult(
                rawSignupToken!,
                token.ClientApplicationId ?? payload.ClientApplicationId,
                payload.ClientId,
                payload.DisplayName,
                payload.PhoneNumber,
                payload.OrganizationName,
                token.OrganizationId ?? payload.OrganizationId,
                payload.CustomFields));
    }

    /// <summary>The unspent, unexpired sign-up token whose raw value is <paramref name="rawToken"/>.</summary>
    public static Task<SqlOSTemporaryToken?> FindAsync(
        ISqlOSAuthServerDbContext context,
        string rawToken,
        DateTime now,
        CancellationToken cancellationToken)
        => context.Set<SqlOSTemporaryToken>()
            .Where(SqlOSTemporaryToken.OfKind(SqlOSTemporaryTokenKinds.PhoneOtpSignup))
            .Where(SqlOSTemporaryToken.Presented(rawToken))
            .Where(SqlOSTemporaryToken.UsableAt(now))
            .FirstOrDefaultAsync(cancellationToken);

    private static bool Answers(PhoneOtpSignupPayload payload, ChallengeBinding binding)
    {
        if (!binding.RequireMatch)
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(binding.AuthorizationRequestId)
            ? string.IsNullOrWhiteSpace(payload.AuthorizationRequestId)
            : string.Equals(payload.AuthorizationRequestId, binding.AuthorizationRequestId, StringComparison.Ordinal);
    }
}

/// <summary>What verifying a phone-code sign-up did.</summary>
internal abstract record PhoneOtpSignupCheck
{
    private PhoneOtpSignupCheck()
    {
    }

    /// <summary>The code was right: <see cref="Token"/> is the (unspent) sign-up token and <see cref="Result"/> the account it carries.</summary>
    public sealed record Verified(SqlOSTemporaryToken Token, SqlOSPhoneOtpSignupVerificationResult Result) : PhoneOtpSignupCheck;

    public sealed record Refused(IdentityRefusal Refusal) : PhoneOtpSignupCheck
    {
        public static Refused InvalidCode { get; } = new(IdentityRefusals.InvalidCode);
    }
}

/// <summary>
/// Completes a phone-code sign-up on any surface: the code proves the number, the account is
/// created with it verified, and the new account signs in.
/// </summary>
/// <remarks>
/// The sign-up runs in one transaction (<see cref="SignupUnitOfWork"/>). A number one account
/// already has is refused, and phone sign-up does not accept an email invitation. A direct login
/// completes for the client the sign-up token was issued to, which must be first-party (#419).
/// </remarks>
internal sealed class CompletePhoneOtpSignUp(
    ISqlOSAuthServerDbContext context,
    SqlOSSettingsService settings,
    SqlOSAdminService admin,
    SqlOSCryptoService crypto,
    SqlOSPhoneOtpService codes,
    IHostSignupHook hook,
    ILoginCompletion completion,
    TimeProvider clock)
{
    /// <summary>
    /// The number the code proved, once it did. The headless surface shows it when the sign-up
    /// fails after the code was verified (7.2.1).
    /// </summary>
    public string? VerifiedPhoneNumber { get; private set; }

    public async Task<SignUpOutcome> ExecuteAsync(CompletePhoneOtpSignUpCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = clock.GetUtcNow().UtcDateTime;
        var directLogin = command.Target is LoginTarget.DirectLogin;
        if (directLogin)
        {
            await SignupTokenClients.EnsureFirstPartyAsync(context, admin, SqlOSTemporaryTokenKinds.PhoneOtpSignup, command.SignupToken, command.Request, now, cancellationToken);
        }

        if (command.CarriesInvitation)
        {
            return new SignUpOutcome.Refused(IdentityRefusals.PhoneSignupWithInvitation);
        }

        var unitOfWork = new SignupUnitOfWork(context);
        return await unitOfWork.RunAsync(
            async () =>
            {
                if (!(await settings.GetResolvedCredentialSettingsAsync(cancellationToken)).PhoneOtpEnabled)
                {
                    return new SignUpOutcome.Refused(IdentityRefusals.PhoneCodesUnavailable);
                }

                var check = await PhoneOtpSignupTokens.VerifyAsync(
                    context,
                    codes,
                    command.SignupToken,
                    command.ChallengeToken,
                    command.Code,
                    ChallengeBinding.For(command.Target),
                    now,
                    cancellationToken);
                if (check is not PhoneOtpSignupCheck.Verified verified)
                {
                    return new SignUpOutcome.Refused(((PhoneOtpSignupCheck.Refused)check).Refusal);
                }

                var verification = verified.Result;
                VerifiedPhoneNumber = verification.PhoneNumber;
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
                var registered = await AccountRegistration.RegisterWithPhoneOtpAsync(
                    context,
                    admin,
                    settings,
                    crypto,
                    verification.DisplayName,
                    verification.PhoneNumber,
                    verification.OrganizationName,
                    authorizationRequest?.OrganizationId ?? verification.OrganizationId,
                    now,
                    cancellationToken);
                if (registered is not SignupRegistrationOutcome.Registered { Registration: var registration })
                {
                    return new SignUpOutcome.Refused(((SignupRegistrationOutcome.Refused)registered).Refusal);
                }

                unitOfWork.Registered(registration.User, registration.CreatedOrganizationId);
                var organizationId = registration.Organizations.FirstOrDefault()?.Id;
                var signup = new SignupCompletion(
                    context,
                    hook,
                    completion,
                    SignupConventions.For(command.Request.Surface, AuthenticationMethods.PhoneOtp));
                await signup.RunHostHookAsync(registration.User, authorizationRequest, organizationId, verification.CustomFields, cancellationToken);
                return await signup.CompleteAsync(
                    new LoginEvidence(registration.User, [AuthenticationMethods.PhoneOtp], [], now),
                    SignupDestinations.For(command.Target, client, organizationId),
                    new SignupRecord(
                        AuthenticationMethods.PhoneOtp,
                        organizationId,
                        command.Request.IpAddress,
                        now,
                        verified.Token,
                        SqlOSTemporaryTokenKinds.PhoneOtpSignup),
                    cancellationToken);
            },
            retriable: false,
            cancellationToken);
    }
}

/// <summary>A phone-code sign-up's code, presented with its sign-up token.</summary>
/// <param name="SignupToken">The token the sign-up's start issued.</param>
/// <param name="ChallengeToken">The challenge the code answers.</param>
/// <param name="Code">The code the person typed.</param>
/// <param name="CarriesInvitation">The surface resolved an email invitation, which a phone sign-up cannot accept.</param>
/// <param name="Target">Where the first login completes; a direct login completes for the token's client.</param>
/// <param name="Request">Where the request came from.</param>
internal sealed record CompletePhoneOtpSignUpCommand(
    string? SignupToken,
    string? ChallengeToken,
    string? Code,
    bool CarriesInvitation,
    LoginTarget Target,
    SqlOSRequestContext Request);
