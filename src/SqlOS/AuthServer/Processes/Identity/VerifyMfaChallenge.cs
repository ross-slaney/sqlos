using Microsoft.EntityFrameworkCore;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Answers an MFA challenge with a second factor, an authenticator code or a recovery code: the
/// hosted AuthPage, the headless API and the public API all verify a challenge here, then the hub
/// continues the login the challenge paused (<see cref="ILoginCompletion"/>).
/// </summary>
/// <remarks>
/// <para>
/// Each surface answers the challenges of its own login: the public API a first-party client's
/// direct login, which is refused for a third-party client before the factor is checked (#419); the
/// hosted AuthPage and the headless API the authorization request the challenge was issued for,
/// whichever request the caller names, which must still be active. A challenge whose policy requires
/// enrollment is answered only by confirming an authenticator bound to it.
/// </para>
/// <para>
/// Every comparison is admitted first (<see cref="IAdmissionGate"/>: per challenge, account and IP
/// address), so a refused admission compares nothing. A wrong factor is settled as a failure,
/// counted on the challenge (the failure that reaches the limit withdraws it) and audited, and
/// answered with one public message. A right factor is spent and saved before anything else
/// (<see cref="SecondFactors"/>); the challenge is then spent with the login the hub completes,
/// whose evidence names the first factor's methods and the second factor's.
/// </para>
/// </remarks>
internal sealed class VerifyMfaChallenge(
    ISqlOSAuthServerDbContext context,
    SqlOSAdminService admin,
    SecondFactors secondFactors,
    IAdmissionGate admission,
    IAuditRecorder audit,
    ILoginCompletion completion,
    SqlOSAuthServerOptions options,
    TimeProvider clock)
{
    public async Task<MfaChallengeOutcome> ExecuteAsync(VerifyMfaChallengeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = clock.GetUtcNow().UtcDateTime;
        var challenge = await PresentedTokens.FindAsync(context, SqlOSTemporaryTokenKinds.MfaChallenge, command.MfaToken, now, cancellationToken);
        if (challenge == null)
        {
            return new MfaChallengeOutcome.Refused(MfaChallenges.Invalid);
        }

        if (challenge.UserId == null || challenge.ClientApplicationId == null)
        {
            return new MfaChallengeOutcome.Refused(MfaChallenges.PayloadInvalid);
        }

        var payload = challenge.ReadPayload(SqlOSTemporaryTokenKinds.MfaChallenge);
        if (payload == null)
        {
            return new MfaChallengeOutcome.Refused(MfaChallenges.PayloadInvalid);
        }

        switch (command.Target)
        {
            case MfaChallengeTarget.DirectLogin:
                if (!string.Equals(payload.Flow, MfaChallenges.Client, StringComparison.Ordinal))
                {
                    return new MfaChallengeOutcome.Refused(MfaChallenges.NotForDirectLogin);
                }

                if (payload.EnrollmentRequired)
                {
                    return new MfaChallengeOutcome.Refused(MfaChallenges.EnrollmentPending);
                }

                await MfaChallenges.EnsureClientFlowIsFirstPartyAsync(context, admin, challenge, command.Request, cancellationToken);
                break;
            case MfaChallengeTarget.AuthorizationRequest:
                if (!string.Equals(payload.Flow, MfaChallenges.Authorization, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(payload.AuthorizationRequestId))
                {
                    return new MfaChallengeOutcome.Refused(MfaChallenges.NotForAuthorization);
                }

                if (payload.EnrollmentRequired)
                {
                    return new MfaChallengeOutcome.Refused(MfaChallenges.EnrollmentPending);
                }

                if (await ActiveAuthorizationRequests.FindAsync(context, payload.AuthorizationRequestId, now, cancellationToken) == null)
                {
                    return new MfaChallengeOutcome.Refused(MfaChallenges.AuthorizationRequestInvalid);
                }

                break;
            default:
                throw new InvalidOperationException($"Unknown MFA challenge target '{command.Target}'.");
        }

        var userId = challenge.UserId;
        var reservationId = await admission.AdmitMfaAttemptAsync(
            challenge,
            AdmissionOrigin.Of(command.Request),
            payload.AuthorizationRequestId,
            cancellationToken);
        SecondFactorCheck check;
        try
        {
            check = await secondFactors.VerifyAsync(userId, command.Code, now, cancellationToken);
            if (check is SecondFactorCheck.Verified)
            {
                await admission.RecordMfaAttemptSucceededAsync(reservationId, CancellationToken.None);
            }
        }
        catch (InvalidOperationException)
        {
            // A factor that could not be checked (an unreadable authenticator secret) counts as a
            // wrong one, as in 7.x.
            check = new SecondFactorCheck.Refused(MfaChallenges.CodeInvalid);
        }

        if (check is not SecondFactorCheck.Verified verified)
        {
            await admission.RecordMfaAttemptFailedAsync(reservationId, CancellationToken.None);
            var attemptCount = await CountFailureAsync(challenge, now, cancellationToken);
            await RecordFailureAsync(
                new MfaChallengeFailed(
                    challenge.Id,
                    userId,
                    challenge.OrganizationId,
                    command.Request.IpAddress,
                    attemptCount,
                    attemptCount >= options.Mfa.Totp.MaxFailedAttemptsPerChallenge),
                cancellationToken);
            return new MfaChallengeOutcome.Refused(MfaChallenges.CodeInvalid);
        }

        // Spent with the login the hub completes next, in its save.
        challenge.Consume(SqlOSTemporaryTokenKinds.MfaChallenge, now);
        var evidence = new LoginEvidence(
            verified.User,
            MfaChallenges.Methods(payload.AuthenticationMethod, verified.Factor),
            [],
            now);
        return new MfaChallengeOutcome.SignedIn(
            evidence,
            await completion.CompleteAsync(evidence, MfaChallenges.ContinuationOf(challenge, payload), cancellationToken));
    }

    /// <summary>
    /// Counts a wrong factor on the challenge; the failure that reaches the limit withdraws it, so
    /// it can never be completed. A concurrent failure that changed the challenge first is counted
    /// again on the challenge as it now is.
    /// </summary>
    /// <returns>The challenge's failures, this one included (or the count it had when it was already withdrawn).</returns>
    private async Task<int> CountFailureAsync(SqlOSTemporaryToken challenge, DateTime now, CancellationToken cancellationToken)
    {
        var kind = SqlOSTemporaryTokenKinds.MfaChallenge;
        while (true)
        {
            var payload = challenge.ReadPayload(kind) ?? throw MfaChallenges.PayloadInvalid.ToException();
            if (challenge.ConsumedAt != null)
            {
                return payload.FailedAttempts;
            }

            var attemptCount = payload.FailedAttempts + 1;
            challenge.ReplacePayload(kind, payload with { FailedAttempts = attemptCount });
            if (attemptCount >= options.Mfa.Totp.MaxFailedAttemptsPerChallenge)
            {
                // The last allowed failure locks the challenge: it is withdrawn, never completed.
                challenge.Retire(now);
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return attemptCount;
            }
            catch (DbUpdateConcurrencyException) when (context is DbContext dbContext)
            {
                dbContext.ChangeTracker.Clear();
                var challengeId = challenge.Id;
                challenge = await context.Set<SqlOSTemporaryToken>()
                    .FirstOrDefaultAsync(x => x.Id == challengeId, cancellationToken)
                    ?? throw MfaChallenges.CodeInvalid.ToException();
            }
        }
    }

    /// <summary>
    /// Audits the failure on its own. The challenge already fails closed, so an audit that cannot be
    /// written does not change the answer.
    /// </summary>
    private async Task RecordFailureAsync(MfaChallengeFailed failure, CancellationToken cancellationToken)
    {
        try
        {
            audit.Record(failure);
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }
}

/// <summary>A second factor presented for an MFA challenge.</summary>
/// <param name="MfaToken">The challenge the hub issued.</param>
/// <param name="Code">An authenticator code or a recovery code.</param>
/// <param name="Target">The login the surface answers challenges for.</param>
/// <param name="Request">Where the request came from.</param>
internal sealed record VerifyMfaChallengeCommand(
    string MfaToken,
    string? Code,
    MfaChallengeTarget Target,
    SqlOSRequestContext Request);

/// <summary>What answering an MFA challenge did.</summary>
internal abstract record MfaChallengeOutcome
{
    private MfaChallengeOutcome()
    {
    }

    /// <summary>The second factor proved the login, and the hub completed it.</summary>
    public sealed record SignedIn(LoginEvidence Evidence, LoginCompletion Completion) : MfaChallengeOutcome;

    /// <summary>The answer was refused; the login stays paused, or ends when the challenge was withdrawn.</summary>
    public sealed record Refused(IdentityRefusal Refusal) : MfaChallengeOutcome;
}
