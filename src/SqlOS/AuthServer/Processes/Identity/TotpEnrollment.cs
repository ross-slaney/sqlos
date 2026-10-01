using Microsoft.EntityFrameworkCore;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Database;
using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Starts an authenticator-app enrollment: an account's own (the host's account settings), or the
/// one an MFA challenge requires before its login completes (the hosted AuthPage, the headless API
/// and the public API all start a challenge's enrollment here).
/// </summary>
/// <remarks>
/// <para>
/// The account gets a new, unconfirmed authenticator holding a protected secret (an earlier
/// unconfirmed one is revoked), and the caller an enrollment token, the secret, its provisioning URI
/// and a QR code. Nothing is trusted until the first code confirms it
/// (<see cref="VerifyTotpEnrollment"/>).
/// </para>
/// <para>
/// An account may enroll when authenticator apps are enabled for it and it may enroll itself or must
/// enroll. A challenge permits an enrollment only when its policy required one, still requires one,
/// allows authenticator apps, and its account has none, and only for the login it was issued for:
/// the client it names, and on the browser surfaces the authorization request the caller names. The
/// enrollment is bound to that challenge. A refused challenge enrollment is audited
/// (<c>user.mfa.enrollment.challenge_rejected</c>) and answered with one message; a third-party client
/// holding a direct-login challenge is refused first (#419).
/// </para>
/// </remarks>
internal sealed class StartTotpEnrollment(
    ISqlOSAuthServerDbContext context,
    SqlOSAdminService admin,
    SqlOSTotpMfaService authenticators,
    IAuditRecorder audit,
    TimeProvider clock)
{
    public static readonly IdentityRefusal NotEnabled = new("totp_enrollment_not_enabled", "Authenticator app enrollment is not enabled.");
    public static readonly IdentityRefusal NotAvailable = new("totp_enrollment_not_available", "Authenticator app enrollment is not available for this account.");

    public async Task<TotpEnrollmentStartOutcome> ExecuteAsync(StartTotpEnrollmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = clock.GetUtcNow().UtcDateTime;
        return command.Target switch
        {
            TotpEnrollmentTarget.Account account => await StartForAccountAsync(account, command.DisplayName, now, cancellationToken),
            TotpEnrollmentTarget.Challenge challenge => await StartForChallengeAsync(challenge, command, now, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown enrollment target '{command.Target.GetType().Name}'.")
        };
    }

    private async Task<TotpEnrollmentStartOutcome> StartForAccountAsync(
        TotpEnrollmentTarget.Account account,
        string? displayName,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var evaluation = await authenticators.Policy.EvaluateAsync(account.UserId, account.OrganizationId, authenticationMethod: null, cancellationToken);
        if (!evaluation.Enabled || !evaluation.AvailableFactors.Contains(SqlOSMfaFactorTypes.Totp, StringComparer.OrdinalIgnoreCase))
        {
            return new TotpEnrollmentStartOutcome.Refused(NotEnabled);
        }

        if (!evaluation.CanSelfEnroll && !evaluation.EnrollmentRequired)
        {
            return new TotpEnrollmentStartOutcome.Refused(NotAvailable);
        }

        return new TotpEnrollmentStartOutcome.Started(await EnrollAsync(
            account.UserId,
            account.OrganizationId,
            clientApplicationId: null,
            displayName,
            challengeBinding: null,
            now,
            cancellationToken));
    }

    private async Task<TotpEnrollmentStartOutcome> StartForChallengeAsync(
        TotpEnrollmentTarget.Challenge target,
        StartTotpEnrollmentCommand command,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var challenge = await PresentedTokens.FindAsync(context, SqlOSTemporaryTokenKinds.MfaChallenge, target.MfaToken, now, cancellationToken);
        if (challenge == null)
        {
            return new TotpEnrollmentStartOutcome.Refused(MfaChallenges.Invalid);
        }

        await MfaChallenges.EnsureClientFlowIsFirstPartyAsync(context, admin, challenge, command.Request, cancellationToken);
        try
        {
            if (await PermittedEnrollmentAsync(challenge, target, now, cancellationToken) is { } payload
                && await EnrollForChallengeAsync(challenge, payload, command.DisplayName, now, cancellationToken) is { } started)
            {
                return new TotpEnrollmentStartOutcome.Started(started);
            }
        }
        catch (InvalidOperationException)
        {
            // An enrollment that fails on the way (a missing account) is refused like any other.
        }

        await RecordRejectionAsync(challenge, cancellationToken);
        return new TotpEnrollmentStartOutcome.Refused(MfaChallenges.EnrollmentNotAuthorized);
    }

    /// <summary>
    /// The challenge's payload when it permits an authenticator enrollment for the login the caller
    /// names: its policy required one with authenticator apps, it belongs to the expected flow (and
    /// request), and it still names its own client (and, on the browser surfaces, a request of that
    /// client). Null otherwise.
    /// </summary>
    private async Task<SqlOSMfaChallengePayload?> PermittedEnrollmentAsync(
        SqlOSTemporaryToken challenge,
        TotpEnrollmentTarget.Challenge target,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (challenge.UserId == null || challenge.ClientApplicationId == null)
        {
            return null;
        }

        var payload = challenge.ReadPayload(SqlOSTemporaryTokenKinds.MfaChallenge);
        var expectedFlow = target.ExpectedFlow;
        if (payload == null
            || !payload.EnrollmentRequired
            || payload.PermittedEnrollmentFactors?.Contains(SqlOSMfaFactorTypes.Totp, StringComparer.OrdinalIgnoreCase) != true
            || !string.Equals(payload.Flow, expectedFlow, StringComparison.Ordinal)
            || (target.AuthorizationRequestId != null
                && !string.Equals(payload.AuthorizationRequestId, target.AuthorizationRequestId, StringComparison.Ordinal)))
        {
            return null;
        }

        var client = await context.Set<SqlOSClientApplication>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == challenge.ClientApplicationId, cancellationToken);
        if (client == null || !string.Equals(client.ClientId, payload.ClientId, StringComparison.Ordinal))
        {
            return null;
        }

        if (string.Equals(expectedFlow, MfaChallenges.Authorization, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(payload.AuthorizationRequestId))
            {
                return null;
            }

            var request = await context.Set<SqlOSAuthorizationRequest>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == payload.AuthorizationRequestId, cancellationToken);
            if (request == null || !string.Equals(request.ClientApplicationId, challenge.ClientApplicationId, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return payload;
    }

    /// <summary>
    /// Enrolls an authenticator bound to <paramref name="challenge"/>, when the account's policy still
    /// requires an enrollment, allows authenticator apps, and the account has none. Null otherwise.
    /// </summary>
    private async Task<SqlOSTotpEnrollmentStartResult?> EnrollForChallengeAsync(
        SqlOSTemporaryToken challenge,
        SqlOSMfaChallengePayload payload,
        string? displayName,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var evaluation = await authenticators.Policy.EvaluateAsync(
            challenge.UserId!,
            challenge.OrganizationId,
            payload.AuthenticationMethod,
            cancellationToken);
        if (!evaluation.EnrollmentRequired
            || evaluation.HasTotp
            || !evaluation.AvailableFactors.Contains(SqlOSMfaFactorTypes.Totp, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return await EnrollAsync(
            challenge.UserId!,
            challenge.OrganizationId,
            challenge.ClientApplicationId,
            displayName,
            new TotpEnrollmentChallengeBinding(
                challenge.Id,
                challenge.UserId!,
                challenge.ClientApplicationId!,
                challenge.OrganizationId,
                payload.Flow,
                payload.ClientId,
                payload.AuthorizationRequestId,
                payload.Resource),
            now,
            cancellationToken);
    }

    /// <summary>
    /// Enrolls a new, unconfirmed authenticator on the account and saves it with its enrollment
    /// token; returns what the person needs to add it to their app.
    /// </summary>
    private async Task<SqlOSTotpEnrollmentStartResult> EnrollAsync(
        string userId,
        string? organizationId,
        string? clientApplicationId,
        string? displayName,
        TotpEnrollmentChallengeBinding? challengeBinding,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var user = await context.GetUserAsync(userId, SqlOSUserParts.Authenticators, cancellationToken);
        var secret = authenticators.NewSecret();
        var authenticator = user.EnrollTotp(authenticators.Protect(secret), displayName, authenticators.Parameters, now);
        var kind = SqlOSTemporaryTokenKinds.TotpEnrollment;
        var enrollment = SqlOSTemporaryToken.Issue(
            kind,
            new TotpEnrollmentPayload(authenticator.Id, challengeBinding),
            new TemporaryTokenBinding(UserId: userId, ClientApplicationId: clientApplicationId, OrganizationId: organizationId),
            kind.Lifetime.Resolve(authenticators.Options.EnrollmentTokenLifetime),
            now);
        context.Set<SqlOSTemporaryToken>().Add(enrollment.Token);
        await context.SaveChangesAsync(cancellationToken);

        var provisioningUri = authenticators.BuildProvisioningUri(user, secret);
        return new SqlOSTotpEnrollmentStartResult(
            enrollment.RawToken,
            authenticator.Id,
            secret,
            provisioningUri,
            SqlOSTotpMfaService.BuildQrCodeDataUrl(provisioningUri),
            now.Add(authenticators.Options.EnrollmentTokenLifetime));
    }

    /// <summary>
    /// Audits the refusal on its own. The enrollment already fails closed, so an audit that cannot
    /// be written does not change the answer.
    /// </summary>
    private async Task RecordRejectionAsync(SqlOSTemporaryToken challenge, CancellationToken cancellationToken)
    {
        try
        {
            audit.Record(new MfaChallengeEnrollmentRejected(
                challenge.Id,
                challenge.UserId,
                challenge.OrganizationId,
                challenge.ClientApplicationId,
                Stage: "start"));
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }
}

/// <summary>An authenticator enrollment to start.</summary>
/// <param name="Target">The account's own enrollment, or a challenge's.</param>
/// <param name="DisplayName">The authenticator's name, or null.</param>
/// <param name="Request">
/// Where the request came from. 7.x checked a challenge's client against the request on the public
/// API only; the browser surfaces start a challenge's enrollment without it.
/// </param>
internal sealed record StartTotpEnrollmentCommand(
    TotpEnrollmentTarget Target,
    string? DisplayName,
    SqlOSRequestContext Request)
{
    /// <summary>
    /// The enrollment an authorization request's MFA challenge requires, started by a browser surface
    /// (the hosted AuthPage or the headless API) for <paramref name="authorizationRequestId"/>. 7.2.1
    /// started it without the HTTP request, so a direct-login challenge presented here is checked
    /// against no request.
    /// </summary>
    public static StartTotpEnrollmentCommand ForAuthorizationChallenge(
        string mfaToken,
        string authorizationRequestId,
        string? displayName,
        SqlOSRequestSurface surface)
        => new(
            new TotpEnrollmentTarget.Challenge(mfaToken, MfaChallengeTarget.AuthorizationRequest, authorizationRequestId),
            displayName,
            SqlOSRequestContext.System with { Surface = surface });
}

/// <summary>Which enrollment: an account's own, or the one an MFA challenge requires.</summary>
internal abstract record TotpEnrollmentTarget
{
    private TotpEnrollmentTarget()
    {
    }

    /// <summary>The account's own enrollment (host code, the account's settings), under the policy of <see cref="OrganizationId"/>.</summary>
    public sealed record Account(string UserId, string? OrganizationId) : TotpEnrollmentTarget;

    /// <summary>
    /// The enrollment the challenge <see cref="MfaToken"/> requires, for the login the surface
    /// answers challenges for: a direct login, or (on the browser surfaces) the authorization request
    /// <see cref="AuthorizationRequestId"/> names.
    /// </summary>
    public sealed record Challenge(string MfaToken, MfaChallengeTarget Login, string? AuthorizationRequestId = null) : TotpEnrollmentTarget
    {
        /// <summary>The flow the challenge must belong to.</summary>
        public string ExpectedFlow => Login == MfaChallengeTarget.DirectLogin ? MfaChallenges.Client : MfaChallenges.Authorization;
    }
}

/// <summary>What starting an enrollment did.</summary>
internal abstract record TotpEnrollmentStartOutcome
{
    private TotpEnrollmentStartOutcome()
    {
    }

    public sealed record Started(SqlOSTotpEnrollmentStartResult Result) : TotpEnrollmentStartOutcome;

    public sealed record Refused(IdentityRefusal Refusal) : TotpEnrollmentStartOutcome;
}

/// <summary>
/// Confirms an authenticator enrollment with the authenticator's first code: an account's own
/// enrollment, or the one an MFA challenge required, which then answers the challenge and completes
/// its login (the hosted AuthPage, the headless API and the public API all confirm here).
/// </summary>
/// <remarks>
/// <para>
/// A code for the authenticator confirms it, records its time step (so the code cannot answer a
/// challenge again), opts the account into MFA unless it already chose, and, when the policy allows
/// recovery codes, replaces the account's unused recovery codes with new ones, returned once.
/// </para>
/// <para>
/// A challenge's enrollment must be confirmed with that challenge, which must still require an
/// enrollment for its account, and both must be bound to the same account, client, organization,
/// flow, request and resource. Confirming spends the enrollment and the challenge together; the hub
/// then completes the login with evidence of the first factor and the authenticator. The
/// confirmation and the login commit together in one transaction, as in 7.2.1: a login that fails
/// leaves the enrollment unconfirmed. A third-party client holding a direct-login challenge is refused
/// first (#419).
/// </para>
/// </remarks>
internal sealed class VerifyTotpEnrollment(
    ISqlOSAuthServerDbContext context,
    SqlOSAdminService admin,
    SqlOSTotpMfaService authenticators,
    ILoginCompletion completion,
    TimeProvider clock)
{
    public static readonly IdentityRefusal EnrollmentInvalidOrExpired = new("totp_enrollment_invalid_or_expired", "Authenticator enrollment is invalid or expired.");
    public static readonly IdentityRefusal EnrollmentInvalid = new("totp_enrollment_invalid", "Authenticator enrollment is invalid.");
    public static readonly IdentityRefusal EnrollmentPayloadInvalid = new("totp_enrollment_payload_invalid", "Authenticator enrollment payload is invalid.");
    public static readonly IdentityRefusal ChallengeBound = new("totp_enrollment_challenge_bound", "Challenge-bound enrollment must be verified with its original MFA challenge.");
    public static readonly IdentityRefusal AlreadyConfirmed = new("totp_enrollment_already_confirmed", "Authenticator enrollment has already been confirmed.");
    public static readonly IdentityRefusal CodeInvalid = new("totp_code_invalid", "Authenticator code is invalid.");
    public static readonly IdentityRefusal ChallengeUsed = new("mfa_enrollment_challenge_used", "MFA enrollment challenge has already been used.");

    public async Task<TotpEnrollmentVerifyOutcome> ExecuteAsync(VerifyTotpEnrollmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = clock.GetUtcNow().UtcDateTime;
        return command.Challenge == null
            ? await ConfirmAccountEnrollmentAsync(command, now, cancellationToken)
            : await ConfirmChallengeEnrollmentAsync(command.Challenge, command, now, cancellationToken);
    }

    private async Task<TotpEnrollmentVerifyOutcome> ConfirmAccountEnrollmentAsync(
        VerifyTotpEnrollmentCommand command,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var enrollment = await PresentedTokens.FindAsync(context, SqlOSTemporaryTokenKinds.TotpEnrollment, command.EnrollmentToken, now, cancellationToken);
        if (enrollment == null)
        {
            return new TotpEnrollmentVerifyOutcome.Refused(EnrollmentInvalidOrExpired);
        }

        if (enrollment.UserId == null)
        {
            return new TotpEnrollmentVerifyOutcome.Refused(EnrollmentInvalid);
        }

        var payload = enrollment.ReadPayload(SqlOSTemporaryTokenKinds.TotpEnrollment);
        if (payload == null)
        {
            return new TotpEnrollmentVerifyOutcome.Refused(EnrollmentPayloadInvalid);
        }

        if (payload.ChallengeBinding != null)
        {
            return new TotpEnrollmentVerifyOutcome.Refused(ChallengeBound);
        }

        return await ConfirmAsync(enrollment, payload, command.Code, challenge: null, now, cancellationToken) switch
        {
            Confirmation.Confirmed confirmed => new TotpEnrollmentVerifyOutcome.Confirmed(confirmed.Result),
            Confirmation.Refused refused => new TotpEnrollmentVerifyOutcome.Refused(refused.Refusal),
            var other => throw new InvalidOperationException($"Unknown confirmation '{other.GetType().Name}'.")
        };
    }

    private async Task<TotpEnrollmentVerifyOutcome> ConfirmChallengeEnrollmentAsync(
        TotpEnrollmentTarget.Challenge target,
        VerifyTotpEnrollmentCommand command,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (target.Login == MfaChallengeTarget.DirectLogin)
        {
            // Outside the enrollment's transaction, so a refused client's audit is not rolled back.
            await MfaChallenges.EnsureClientFlowIsFirstPartyAsync(
                context,
                admin,
                await PresentedTokens.FindAsync(context, SqlOSTemporaryTokenKinds.MfaChallenge, target.MfaToken, now, cancellationToken),
                command.Request,
                cancellationToken);
        }

        return await IdentityTransactions.RunAsync<TotpEnrollmentVerifyOutcome>(
            context,
            async () =>
            {
                if (target.Login == MfaChallengeTarget.AuthorizationRequest
                    && await ActiveAuthorizationRequests.FindAsync(context, target.AuthorizationRequestId, now, cancellationToken) == null)
                {
                    return new TotpEnrollmentVerifyOutcome.Refused(MfaChallenges.AuthorizationRequestInvalid);
                }

                var bound = await BoundEnrollmentAsync(target, command.EnrollmentToken, now, cancellationToken);
                if (bound == null)
                {
                    return new TotpEnrollmentVerifyOutcome.Refused(MfaChallenges.EnrollmentNotAuthorized);
                }

                var confirmation = await ConfirmAsync(bound.Enrollment, bound.EnrollmentPayload, command.Code, bound.Challenge, now, cancellationToken);
                if (confirmation is not Confirmation.Confirmed confirmed)
                {
                    return new TotpEnrollmentVerifyOutcome.Refused(((Confirmation.Refused)confirmation).Refusal);
                }

                var evidence = new LoginEvidence(
                    confirmed.User,
                    MfaChallenges.Methods(bound.ChallengePayload.AuthenticationMethod, SqlOSMfaFactorTypes.Totp),
                    [],
                    now);
                var completed = await completion.CompleteAsync(
                    evidence,
                    MfaChallenges.ContinuationOf(bound.Challenge, bound.ChallengePayload),
                    cancellationToken);
                return new TotpEnrollmentVerifyOutcome.SignedIn(confirmed.Result, evidence, completed);
            },
            commits: static outcome => outcome is TotpEnrollmentVerifyOutcome.SignedIn,
            cancellationToken);
    }

    /// <summary>
    /// The enrollment and its challenge when <paramref name="enrollmentToken"/> was started for the
    /// challenge the caller presents, the challenge still permits it for the expected login, and the
    /// account's policy still requires an enrollment with authenticator apps and has none. Null
    /// otherwise.
    /// </summary>
    private async Task<BoundEnrollment?> BoundEnrollmentAsync(
        TotpEnrollmentTarget.Challenge target,
        string enrollmentToken,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target.MfaToken))
        {
            return null;
        }

        var challenge = await PresentedTokens.FindAsync(context, SqlOSTemporaryTokenKinds.MfaChallenge, target.MfaToken, now, cancellationToken);
        if (challenge == null)
        {
            return null;
        }

        var enrollment = await PresentedTokens.FindAsync(context, SqlOSTemporaryTokenKinds.TotpEnrollment, enrollmentToken, now, cancellationToken);
        var challengePayload = challenge.ReadPayload(SqlOSTemporaryTokenKinds.MfaChallenge);
        var enrollmentPayload = enrollment?.ReadPayload(SqlOSTemporaryTokenKinds.TotpEnrollment);
        var binding = enrollmentPayload?.ChallengeBinding;
        if (enrollment == null || challengePayload == null || enrollmentPayload == null || binding == null)
        {
            return null;
        }

        if (challenge.UserId == null
            || challenge.ClientApplicationId == null
            || !challengePayload.EnrollmentRequired
            || challengePayload.PermittedEnrollmentFactors?.Contains(SqlOSMfaFactorTypes.Totp, StringComparer.OrdinalIgnoreCase) != true
            || !string.Equals(enrollment.UserId, challenge.UserId, StringComparison.Ordinal)
            || !string.Equals(enrollment.ClientApplicationId, challenge.ClientApplicationId, StringComparison.Ordinal)
            || !string.Equals(enrollment.OrganizationId, challenge.OrganizationId, StringComparison.Ordinal)
            || !string.Equals(binding.ChallengeTokenId, challenge.Id, StringComparison.Ordinal)
            || !string.Equals(binding.UserId, challenge.UserId, StringComparison.Ordinal)
            || !string.Equals(binding.ClientApplicationId, challenge.ClientApplicationId, StringComparison.Ordinal)
            || !string.Equals(binding.OrganizationId, challenge.OrganizationId, StringComparison.Ordinal)
            || !string.Equals(binding.Flow, challengePayload.Flow, StringComparison.Ordinal)
            || !string.Equals(challengePayload.Flow, target.ExpectedFlow, StringComparison.Ordinal)
            || !string.Equals(binding.ClientId, challengePayload.ClientId, StringComparison.Ordinal)
            || !string.Equals(binding.AuthorizationRequestId, challengePayload.AuthorizationRequestId, StringComparison.Ordinal)
            || (target.AuthorizationRequestId != null
                && !string.Equals(challengePayload.AuthorizationRequestId, target.AuthorizationRequestId, StringComparison.Ordinal))
            || !string.Equals(binding.Resource, challengePayload.Resource, StringComparison.Ordinal))
        {
            return null;
        }

        var evaluation = await authenticators.Policy.EvaluateAsync(
            challenge.UserId,
            challenge.OrganizationId,
            challengePayload.AuthenticationMethod,
            cancellationToken);
        if (!evaluation.EnrollmentRequired
            || evaluation.HasTotp
            || !evaluation.AvailableFactors.Contains(SqlOSMfaFactorTypes.Totp, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return new BoundEnrollment(challenge, challengePayload, enrollment, enrollmentPayload);
    }

    /// <summary>
    /// Confirms the enrollment's authenticator with <paramref name="code"/>, spends the enrollment
    /// (and the challenge it answers), issues new recovery codes when the policy allows them, and
    /// saves. Another confirmation that spent either first refuses this one.
    /// </summary>
    private async Task<Confirmation> ConfirmAsync(
        SqlOSTemporaryToken enrollment,
        TotpEnrollmentPayload payload,
        string code,
        SqlOSTemporaryToken? challenge,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var user = await context.FindUserAsync(
            enrollment.UserId,
            SqlOSUserParts.Authenticators | SqlOSUserParts.RecoveryCodes | SqlOSUserParts.MfaPolicyOverride,
            cancellationToken);
        if (user == null || user.FindAuthenticator(payload.AuthenticatorId) is not { IsTotp: true } authenticator)
        {
            return new Confirmation.Refused(EnrollmentInvalid);
        }

        if (authenticator.IsConfirmed)
        {
            return new Confirmation.Refused(AlreadyConfirmed);
        }

        var secret = authenticators.Unprotect(authenticator.SecretProtected);
        if (!authenticators.TryMatchCode(secret, code, authenticator.PeriodSeconds, authenticator.Digits, now, out var matchedStep))
        {
            return new Confirmation.Refused(CodeInvalid);
        }

        // Confirming opts the account into MFA unless it already chose.
        user.ConfirmTotp(authenticator.Id, matchedStep, now);
        enrollment.Consume(SqlOSTemporaryTokenKinds.TotpEnrollment, now);
        challenge?.Consume(SqlOSTemporaryTokenKinds.MfaChallenge, now);

        var recoveryCodes = await ReplaceRecoveryCodesAsync(user, enrollment.OrganizationId, now, cancellationToken);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new Confirmation.Refused(ChallengeUsed);
        }
        catch (DbUpdateException exception) when (SqlOSDatabaseErrors.IsUniqueConstraintViolation(exception))
        {
            return new Confirmation.Refused(ChallengeUsed);
        }

        return new Confirmation.Confirmed(user, new SqlOSTotpEnrollmentVerifyResult(authenticator.Id, recoveryCodes));
    }

    /// <summary>New recovery codes for the account, when its policy allows them; the raw codes, returned once.</summary>
    private async Task<string[]> ReplaceRecoveryCodesAsync(SqlOSUser user, string? organizationId, DateTime now, CancellationToken cancellationToken)
    {
        var evaluation = await authenticators.Policy.EvaluateAsync(user.Id, organizationId, authenticationMethod: null, cancellationToken);
        if (!evaluation.RecoveryCodesEnabled
            || !evaluation.AvailableFactors.Contains(SqlOSMfaFactorTypes.RecoveryCode, StringComparer.OrdinalIgnoreCase))
        {
            return [];
        }

        var rawCodes = authenticators.NewRecoveryCodes();
        user.IssueRecoveryCodes(rawCodes.Select(SqlOSTotpMfaService.NormalizeRecoveryCode).ToArray(), now);
        return rawCodes;
    }

    private sealed record BoundEnrollment(
        SqlOSTemporaryToken Challenge,
        SqlOSMfaChallengePayload ChallengePayload,
        SqlOSTemporaryToken Enrollment,
        TotpEnrollmentPayload EnrollmentPayload);

    private abstract record Confirmation
    {
        public sealed record Confirmed(SqlOSUser User, SqlOSTotpEnrollmentVerifyResult Result) : Confirmation;

        public sealed record Refused(IdentityRefusal Refusal) : Confirmation;
    }
}

/// <summary>An authenticator's first code, presented for its enrollment.</summary>
/// <param name="EnrollmentToken">The enrollment the code confirms.</param>
/// <param name="Code">The authenticator's code.</param>
/// <param name="Challenge">The challenge the enrollment was started for, or null for an account's own enrollment.</param>
/// <param name="Request">Where the request came from.</param>
internal sealed record VerifyTotpEnrollmentCommand(
    string EnrollmentToken,
    string Code,
    TotpEnrollmentTarget.Challenge? Challenge,
    SqlOSRequestContext Request);

/// <summary>What confirming an enrollment did.</summary>
internal abstract record TotpEnrollmentVerifyOutcome
{
    private TotpEnrollmentVerifyOutcome()
    {
    }

    /// <summary>The account's own authenticator was confirmed; <see cref="Result"/> holds its recovery codes.</summary>
    public sealed record Confirmed(SqlOSTotpEnrollmentVerifyResult Result) : TotpEnrollmentVerifyOutcome;

    /// <summary>
    /// The challenge's authenticator was confirmed and answered the challenge: the hub completed the
    /// login (<see cref="Completion"/>).
    /// </summary>
    public sealed record SignedIn(SqlOSTotpEnrollmentVerifyResult Result, LoginEvidence Evidence, LoginCompletion Completion) : TotpEnrollmentVerifyOutcome;

    /// <summary>Nothing was confirmed.</summary>
    public sealed record Refused(IdentityRefusal Refusal) : TotpEnrollmentVerifyOutcome;
}
