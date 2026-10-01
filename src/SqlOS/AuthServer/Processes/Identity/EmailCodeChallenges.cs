using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Issues and verifies email-code challenges: the step every email-code process shares (sign-in
/// and sign-up, start and verify). The challenge aggregate holds the rules; this step loads it,
/// admits and sends through the email-code channel (<see cref="SqlOSEmailOtpService"/>), and saves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Issuing.</b> The send is admitted atomically before anything is written, so requests sent
/// together can never exceed a limit between them (#424); a resend the cooldown refuses gives its
/// admission back. A newer challenge supersedes the open ones of the same address and context. The
/// challenge is saved before its code is sent, and the start (or the failed delivery) is saved
/// after: a code for an existing account goes only to the address stored on that account (#422).
/// </para>
/// <para>
/// <b>Verifying.</b> The attempt is spent in the database before the code is compared (#424); a
/// wrong code is recorded, and invalidates the challenge when it spent the last attempt. A right
/// code completes the challenge, which proves its recipient's mailbox: a sign-in claims an
/// unverified address with that proof, and a sign-up refuses an address an account already owns.
/// </para>
/// </remarks>
internal sealed class EmailCodeChallenges(ISqlOSAuthServerDbContext context, SqlOSEmailOtpService channel)
{
    /// <summary>Issues a challenge for <paramref name="request"/> and sends its code.</summary>
    public async Task<EmailCodeIssue> IssueAsync(EmailCodeIssueRequest request, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ParseAddress(request.Email) is not { } requestedAddress)
        {
            return new EmailCodeIssue.Refused(InvalidAddress(request.Email));
        }

        var normalizedEmail = requestedAddress.Canonical;
        var challengeRequest = new EmailOtpChallengeRequest(
            requestedAddress,
            request.Purpose,
            request.Context,
            request.Request.IpAddress,
            request.Request.UserAgent);

        var admission = await channel.Admission.AdmitEmailCodeAsync(
            requestedAddress,
            AdmissionOrigin.Of(request.Request),
            request.Context.ClientApplicationId,
            now,
            cancellationToken);
        if (!admission.Admitted)
        {
            channel.AuditRecorder.Record(new EmailOtpSendRateLimited(
                request.Purpose,
                Masked.Email(requestedAddress.Address),
                challengeRequest.IpAddress,
                admission.RefusedLimit!,
                request.Context.ClientApplicationId,
                request.Context.RequestedOrganizationId));
            await context.SaveChangesAsync(cancellationToken);
            return new EmailCodeIssue.Refused(new IdentityRefusal("rate_limited", "Too many sign-in code requests. Try again later."));
        }

        var options = channel.Options;
        var recentChallenges = (await context.Set<SqlOSEmailOtpChallenge>()
                .Where(x => x.NormalizedEmail == normalizedEmail && x.CreatedAt >= now.AddHours(-1))
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync(cancellationToken))
            .Where(x => string.Equals(x.NormalizedEmail, normalizedEmail, StringComparison.Ordinal))
            .ToList();
        var latestContextChallenge = recentChallenges
            .FirstOrDefault(x => x.WasStartedIn(request.Context) && !x.IsInvalidated);
        if (latestContextChallenge != null && latestContextChallenge.WasSentWithin(options.ResendCooldown, now))
        {
            // A resend the cooldown refuses sends nothing, so it does not count against a limit.
            await channel.Admission.WithdrawAsync(admission, now, cancellationToken);
            return new EmailCodeIssue.Refused(new IdentityRefusal(
                "resend_cooldown",
                $"Wait {(int)Math.Ceiling(options.ResendCooldown.TotalSeconds)} seconds before requesting another code."));
        }

        var emailRecord = await context.Set<SqlOSUserEmail>()
            .Include(x => x.User)
            .FindByNormalizedEmailAsync(normalizedEmail, request.Email, cancellationToken);

        var activeChallenges = await context.Set<SqlOSEmailOtpChallenge>()
            .Where(x => x.NormalizedEmail == normalizedEmail
                && x.ConsumedAt == null
                && x.InvalidatedAt == null
                && x.ExpiresAt > now
                && x.AuthorizationRequestId == request.Context.AuthorizationRequestId
                && x.ClientApplicationId == request.Context.ClientApplicationId
                && x.RequestedOrganizationId == request.Context.RequestedOrganizationId)
            .ToListAsync(cancellationToken);
        foreach (var activeChallenge in activeChallenges.Where(x => string.Equals(x.NormalizedEmail, normalizedEmail, StringComparison.Ordinal)))
        {
            activeChallenge.Supersede(now);
        }

        // A code for an existing account is only ever delivered to the address stored on that
        // account, never to the typed spelling. Without an account, the typed address is the
        // address being signed up.
        var issued = SqlOSEmailOtpChallenge.Issue(
            challengeRequest,
            emailRecord,
            new EmailOtpChallengeSettings(options.CodeLength, options.MaxAttempts, options.ChallengeLifetime),
            now);
        var challenge = issued.Challenge;
        context.Set<SqlOSEmailOtpChallenge>().Add(challenge);
        await context.SaveChangesAsync(cancellationToken);

        var codeSent = (emailRecord?.User != null && emailRecord.User.IsActive) || request.SendWhenNoAccount;
        if (codeSent)
        {
            try
            {
                await channel.SendCodeAsync(issued, request.Purpose, cancellationToken);
            }
            catch
            {
                challenge.FailDelivery(challengeRequest, now);
                await context.SaveChangesAsync(cancellationToken);
                return new EmailCodeIssue.Refused(new IdentityRefusal("delivery_failed", "We couldn't send a sign-in code right now."));
            }
        }

        challenge.RecordStart(challengeRequest, codeSent);
        await context.SaveChangesAsync(cancellationToken);

        var maskedEmail = Masked.Email(requestedAddress.Address);
        return new EmailCodeIssue.Issued(new SqlOSEmailOtpStartResult(
            issued.ChallengeToken,
            requestedAddress.Address,
            maskedEmail,
            request.Purpose == EmailOtpPurposes.Signup
                ? $"Check {maskedEmail} for a sign-up code."
                : $"If an account exists for {maskedEmail}, check your email for a sign-in code.",
            challenge.ExpiresAt,
            challenge.LastSentAt.Add(options.ResendCooldown)));
    }

    /// <summary>
    /// Verifies a code against its challenge. A sign-in's attempt commits on its own; a sign-up's
    /// commits with the sign-up's transaction (<see cref="EmailOtpAttemptScope.SignupTransaction"/>).
    /// </summary>
    public async Task<EmailCodeCheck> VerifyAsync(EmailCodeVerifyRequest request, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rawChallengeToken = request.ChallengeToken?.Trim();
        var normalizedCode = NormalizeCode(request.Code);
        if (rawChallengeToken is null || normalizedCode is null)
        {
            return EmailCodeCheck.Refused.InvalidCode;
        }

        var challengeHash = HashedSecret.Sha256(rawChallengeToken).Hash;
        var challenge = await context.Set<SqlOSEmailOtpChallenge>()
            .Include(x => x.User)
            .Include(x => x.UserEmail)
            .Include(x => x.AuthorizationRequest)
            .ThenInclude(x => x!.ClientApplication)
            .Include(x => x.ClientApplication)
            .FirstOrDefaultAsync(x => x.ChallengeTokenHash == challengeHash, cancellationToken);
        if (challenge is null
            || !challenge.IsOpen(now)
            // The code was delivered to an address that is no longer this account's address.
            || !challenge.IsStillAddressedToItsAccount()
            || (request.Binding.RequireMatch && !challenge.AnswersAuthorizationRequest(request.Binding.AuthorizationRequestId)))
        {
            return EmailCodeCheck.Refused.InvalidCode;
        }

        var reservation = await channel.Attempts.TryReserveAsync(
            challenge,
            now,
            request.SignUp ? EmailOtpAttemptScope.SignupTransaction : EmailOtpAttemptScope.Independent,
            cancellationToken);
        if (reservation is null)
        {
            return EmailCodeCheck.Refused.InvalidCode;
        }

        if (challenge.RegisterAttempt(reservation, rawChallengeToken, normalizedCode) == EmailOtpAttemptOutcome.Rejected)
        {
            var exhausted = await channel.Attempts.TryExhaustAsync(challenge, reservation, now, cancellationToken);
            challenge.RejectCode(attemptsExhausted: exhausted);
            await context.SaveChangesAsync(cancellationToken);
            return EmailCodeCheck.Refused.InvalidCode;
        }

        var completion = SqlOSTrackedChangeSnapshot.Capture(context);
        var ownership = challenge.Complete(now);
        if (challenge.UserEmail != null && challenge.User is { IsActive: true })
        {
            // The code proved the mailbox. An unverified address is claimed: whatever was
            // attached before the owner proved it is evicted in this same save.
            await ClaimEmailOwnership.StageAsync(
                context,
                challenge.User,
                ownership,
                PresentedCredentials.None,
                now,
                cancellationToken);
        }

        if (challenge.User != null && challenge.UserEmail != null)
        {
            // The address the code went to becomes the account's default email.
            await context.LoadUserPartsAsync(challenge.User, SqlOSUserParts.Emails, cancellationToken);
            challenge.User.MakeDefaultEmail(ownership, now);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent verification spent the challenge first. Nothing this one staged may
            // reach a later save of the same unit of work.
            completion?.Revert(context);
            ((DbContext)context).Entry(challenge).State = EntityState.Detached;
            return EmailCodeCheck.Refused.InvalidCode;
        }

        if (request.SignUp && await FindExistingAccountRefusalAsync(challenge, cancellationToken) is { } refusal)
        {
            channel.AuditRecorder.Record(refusal);
            await context.SaveChangesAsync(cancellationToken);
            return EmailCodeCheck.Refused.InvalidCode;
        }

        return new EmailCodeCheck.Verified(challenge, ownership);
    }

    /// <summary>
    /// The address an email-code request names, as 7.x read it: trimmed and in its canonical
    /// display form; null when it is blank or not a valid address.
    /// </summary>
    internal static EmailAddress? ParseAddress(string? email)
    {
        var trimmedEmail = email?.Trim();
        return !string.IsNullOrWhiteSpace(trimmedEmail)
               && SqlOSEmailAddress.TryCanonicalize(trimmedEmail, out var address, out _)
            ? EmailAddress.Parse(address)
            : null;
    }

    /// <summary>Why <paramref name="email"/> is not an address a code can go to.</summary>
    internal static IdentityRefusal InvalidAddress(string? email)
        => string.IsNullOrWhiteSpace(email?.Trim())
            ? new IdentityRefusal("email_required", "Email address is required.")
            : new IdentityRefusal("email_invalid", SqlOSEmailAddress.InvalidEmailMessage);

    /// <summary>The digits of a presented code; null when there are none.</summary>
    private static string? NormalizeCode(string? value)
    {
        var normalized = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    /// <summary>
    /// A sign-up code proves a mailbox, but never creates a second account for it: the challenge
    /// must not be bound to an account, and no account may have claimed the address meanwhile.
    /// </summary>
    /// <returns>The refusal to record, or <see langword="null"/> when the sign-up may go on.</returns>
    private async Task<EmailOtpSignupRejectedForExistingEmail?> FindExistingAccountRefusalAsync(
        SqlOSEmailOtpChallenge challenge,
        CancellationToken cancellationToken)
    {
        var reason = challenge.User != null
            ? "challenge_bound_to_existing_user"
            : await context.Set<SqlOSUserEmail>()
                .AsNoTracking()
                .FindByNormalizedEmailAsync(challenge.NormalizedEmail, challenge.Email, cancellationToken) != null
                ? "email_claimed_after_challenge_started"
                : null;
        return reason == null
            ? null
            : new EmailOtpSignupRejectedForExistingEmail(
                Masked.Email(challenge.Email),
                challenge.IpAddress,
                challenge.ClientApplicationId,
                challenge.AuthorizationRequestId,
                reason);
    }
}

/// <summary>One email-code challenge to issue.</summary>
/// <param name="Email">The address as the request typed it.</param>
/// <param name="Purpose"><see cref="EmailOtpPurposes.Login"/> or <see cref="EmailOtpPurposes.Signup"/>.</param>
/// <param name="Context">The sign-in context the challenge belongs to.</param>
/// <param name="SendWhenNoAccount">A sign-up sends its code to an address no account has; a sign-in does not.</param>
/// <param name="Request">Where the request came from.</param>
internal sealed record EmailCodeIssueRequest(
    string Email,
    string Purpose,
    EmailOtpChallengeContext Context,
    bool SendWhenNoAccount,
    SqlOSRequestContext Request);

/// <summary>A code presented against its challenge.</summary>
/// <param name="ChallengeToken">The challenge the code answers.</param>
/// <param name="Code">The code the person typed.</param>
/// <param name="Binding">The authorization request the challenge must belong to.</param>
/// <param name="SignUp">A sign-up code: verified inside the sign-up transaction, and refused for an address an account owns.</param>
internal sealed record EmailCodeVerifyRequest(string? ChallengeToken, string? Code, ChallengeBinding Binding, bool SignUp);

/// <summary>
/// The authorization request a presented code or link must answer: the same request, or none when
/// the surface has none. A direct login does not require a match.
/// </summary>
internal sealed record ChallengeBinding(string? AuthorizationRequestId, bool RequireMatch)
{
    /// <summary>A surface's binding: its authorization request, none for the hosted AuthPage's own sign-in, and no match for a direct login.</summary>
    public static ChallengeBinding For(LoginTarget target) => target switch
    {
        LoginTarget.AuthorizationRequest request => new(request.Request.Id, RequireMatch: true),
        LoginTarget.Browser => new(null, RequireMatch: true),
        _ => new(null, RequireMatch: false)
    };
}

/// <summary>What issuing an email-code challenge did.</summary>
internal abstract record EmailCodeIssue
{
    private EmailCodeIssue()
    {
    }

    /// <summary>The challenge was issued; <see cref="Result"/> is what the requester is told.</summary>
    public sealed record Issued(SqlOSEmailOtpStartResult Result) : EmailCodeIssue;

    /// <summary>No code went out.</summary>
    public sealed record Refused(IdentityRefusal Refusal) : EmailCodeIssue;
}

/// <summary>What verifying an email code did.</summary>
internal abstract record EmailCodeCheck
{
    private EmailCodeCheck()
    {
    }

    /// <summary>The code was right: the challenge is spent, and <see cref="Ownership"/> proves its recipient's mailbox.</summary>
    public sealed record Verified(SqlOSEmailOtpChallenge Challenge, OwnershipProof Ownership) : EmailCodeCheck;

    /// <summary>The code was refused, with the one public answer every refused code gets.</summary>
    public sealed record Refused(IdentityRefusal Refusal) : EmailCodeCheck
    {
        public static Refused InvalidCode { get; } = new(IdentityRefusals.InvalidCode);
    }
}
