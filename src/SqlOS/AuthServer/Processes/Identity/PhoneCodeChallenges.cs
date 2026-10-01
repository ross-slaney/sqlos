using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Issues and verifies phone-code challenges: the step the phone-code processes share (sign-in,
/// sign-up and number enrollment). The challenge aggregate holds the rules; this step loads it,
/// admits the send and talks to the delivery provider through the phone-code channel
/// (<see cref="SqlOSPhoneOtpService"/>), and saves.
/// </summary>
/// <remarks>
/// The send is admitted atomically before anything is written (#424). The provider sends the code
/// to the challenge's recipient and checks a code against that stored recipient, never a number
/// from the request. A challenge the provider never started, or a code it rejects, invalidates the
/// challenge.
/// </remarks>
internal sealed class PhoneCodeChallenges(ISqlOSAuthServerDbContext context, SqlOSPhoneOtpService channel)
{
    /// <summary>Issues a challenge for <paramref name="request"/> and starts its delivery.</summary>
    public async Task<PhoneCodeIssue> IssueAsync(PhoneCodeIssueRequest request, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var phoneNumber = channel.Normalize(request.PhoneNumber);
        if (phoneNumber is PhoneNumberCheck.Invalid invalid)
        {
            return new PhoneCodeIssue.Refused(invalid.Refusal);
        }

        var normalized = ((PhoneNumberCheck.Valid)phoneNumber).PhoneNumber;
        var ipAddress = request.Request.IpAddress;
        var phoneHash = HashedSecret.Sha256(normalized.E164).Hash;
        var maskedPhone = Masked.Phone(normalized.E164);
        var challengeContext = request.Context;

        var phoneRecord = await context.Set<SqlOSUserPhoneNumber>()
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.PhoneNumberHash == phoneHash && x.RemovedAt == null && x.IsVerified, cancellationToken);

        var effectiveUserId = request.UserId ?? phoneRecord?.UserId;
        var effectiveUserPhoneNumberId = phoneRecord?.Id;
        var admission = await channel.Admission.AdmitPhoneCodeAsync(
            phoneHash,
            effectiveUserId,
            AdmissionOrigin.Of(request.Request),
            challengeContext.ClientApplicationId,
            now,
            cancellationToken);
        if (!admission.Admitted)
        {
            channel.AuditRecorder.Record(new PhoneOtpSendRateLimited(
                request.Purpose,
                maskedPhone,
                ipAddress,
                admission.RefusedLimit ?? "phone",
                challengeContext.ClientApplicationId,
                challengeContext.RequestedOrganizationId));
            await context.SaveChangesAsync(cancellationToken);
            return new PhoneCodeIssue.Refused(new IdentityRefusal("rate_limited", "Too many sign-in code requests. Try again later."));
        }

        var options = channel.Options;
        var recentChallenges = await context.Set<SqlOSPhoneOtpChallenge>()
            .Where(x => x.PhoneNumberHash == phoneHash && x.CreatedAt >= now.Subtract(options.RateLimitWindow))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        var latestContextChallenge = recentChallenges
            .FirstOrDefault(x => x.WasStartedIn(challengeContext, request.Purpose) && !x.IsInvalidated);
        if (latestContextChallenge != null && latestContextChallenge.WasSentWithin(options.ResendCooldown, now))
        {
            return new PhoneCodeIssue.Refused(new IdentityRefusal(
                "resend_cooldown",
                $"Wait {(int)Math.Ceiling(options.ResendCooldown.TotalSeconds)} seconds before requesting another code."));
        }

        var activeChallenges = await context.Set<SqlOSPhoneOtpChallenge>()
            .Where(x => x.PhoneNumberHash == phoneHash
                && x.ConsumedAt == null
                && x.InvalidatedAt == null
                && x.ExpiresAt > now
                && x.AuthorizationRequestId == challengeContext.AuthorizationRequestId
                && x.ClientApplicationId == challengeContext.ClientApplicationId
                && x.RequestedOrganizationId == challengeContext.RequestedOrganizationId
                && x.Purpose == request.Purpose)
            .ToListAsync(cancellationToken);
        foreach (var activeChallenge in activeChallenges)
        {
            activeChallenge.Supersede(now);
        }

        var issued = SqlOSPhoneOtpChallenge.Issue(
            new PhoneOtpChallengeRequest(
                normalized,
                request.Purpose,
                challengeContext,
                effectiveUserId,
                effectiveUserPhoneNumberId,
                ipAddress,
                request.Request.UserAgent),
            channel.Protect(normalized.E164),
            options.ChallengeLifetime,
            now);
        var challenge = issued.Challenge;
        context.Set<SqlOSPhoneOtpChallenge>().Add(challenge);
        await context.SaveChangesAsync(cancellationToken);

        var shouldSend = (phoneRecord?.User != null && phoneRecord.User.IsActive) || request.SendWhenNoAccount;
        if (shouldSend)
        {
            // The provider sends to the challenge's recipient, the only number the code goes to.
            var delivery = await channel.DeliveryChannel.StartAsync(
                issued.Recipient.E164,
                new SqlOSOtpDeliveryContext(request.Purpose, challengeContext.ClientApplicationId, challengeContext.AuthorizationRequestId, ipAddress, challenge.UserAgent),
                cancellationToken);
            if (!delivery.Accepted)
            {
                challenge.FailDelivery(delivery, now);
                await context.SaveChangesAsync(cancellationToken);
                return new PhoneCodeIssue.Refused(new IdentityRefusal("delivery_failed", "We couldn't send a sign-in code right now."));
            }

            challenge.RecordProviderStart(delivery);
        }
        else
        {
            challenge.RecordStartWithoutSending();
        }

        await context.SaveChangesAsync(cancellationToken);

        return new PhoneCodeIssue.Issued(new SqlOSPhoneOtpStartResult(
            issued.ChallengeToken,
            normalized.E164,
            maskedPhone,
            request.Purpose == PhoneOtpPurposes.Signup
                ? $"Check {maskedPhone} for a sign-up code."
                : request.Purpose == PhoneOtpPurposes.Enrollment
                    ? $"Check {maskedPhone} for a phone verification code."
                    : SqlOSPhoneOtpService.PublicStartMessage,
            challenge.ExpiresAt,
            challenge.LastSentAt.Add(options.ResendCooldown)));
    }

    /// <summary>
    /// Checks a code against its challenge with the provider. A challenge the provider never
    /// started, or a code it rejects, invalidates the challenge; an approved code spends it.
    /// </summary>
    public async Task<PhoneCodeCheck> VerifyAsync(PhoneCodeVerifyRequest request, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rawChallengeToken = request.ChallengeToken?.Trim();
        var normalizedCode = NormalizeCode(request.Code);
        if (rawChallengeToken is null || normalizedCode is null)
        {
            return PhoneCodeCheck.Refused.InvalidCode;
        }

        var challengeHash = HashedSecret.Sha256(rawChallengeToken).Hash;
        var challenge = await context.Set<SqlOSPhoneOtpChallenge>()
            .Include(x => x.User)
            .Include(x => x.UserPhoneNumber)
            .Include(x => x.AuthorizationRequest)
            .ThenInclude(x => x!.ClientApplication)
            .Include(x => x.ClientApplication)
            .FirstOrDefaultAsync(x => x.ChallengeTokenHash == challengeHash, cancellationToken);
        if (challenge is null
            || !challenge.IsOpen(now)
            || !challenge.IsFor(request.Purpose)
            || (request.Binding.RequireMatch && !challenge.AnswersAuthorizationRequest(request.Binding.AuthorizationRequestId)))
        {
            return PhoneCodeCheck.Refused.InvalidCode;
        }

        if (!challenge.ProviderStarted)
        {
            challenge.RejectCode(SqlOSPhoneOtpChallenge.NotStartedReason, now);
            await context.SaveChangesAsync(cancellationToken);
            return PhoneCodeCheck.Refused.InvalidCode;
        }

        // The provider checks the code against the stored recipient, never a number from the request.
        var check = await channel.DeliveryChannel.CheckAsync(
            channel.Unprotect(challenge.PhoneNumberEncrypted),
            normalizedCode,
            new SqlOSOtpDeliveryContext(
                challenge.Purpose,
                challenge.ClientApplicationId,
                challenge.AuthorizationRequestId,
                challenge.IpAddress,
                challenge.UserAgent,
                challenge.ProviderChallengeId),
            cancellationToken);
        if (challenge.RegisterCheck(check) == PhoneOtpCheckOutcome.Rejected)
        {
            challenge.RejectCode(check.SanitizedError ?? check.ProviderStatus ?? SqlOSPhoneOtpChallenge.ProviderRejectedReason, now);
            await context.SaveChangesAsync(cancellationToken);
            return PhoneCodeCheck.Refused.InvalidCode;
        }

        challenge.Complete(now);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent verification spent the challenge first; nothing this one staged may
            // reach a later save of the same unit of work.
            ((DbContext)context).Entry(challenge).State = EntityState.Detached;
            return PhoneCodeCheck.Refused.InvalidCode;
        }

        return new PhoneCodeCheck.Verified(challenge);
    }

    /// <summary>The digits of a presented code; null when there are none.</summary>
    private static string? NormalizeCode(string? value)
    {
        var normalized = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

/// <summary>One phone-code challenge to issue.</summary>
/// <param name="PhoneNumber">The number as the request typed it.</param>
/// <param name="Purpose">One of <see cref="PhoneOtpPurposes"/>.</param>
/// <param name="Context">The sign-in context the challenge belongs to.</param>
/// <param name="UserId">The enrolling account; null for a sign-in or sign-up, whose account is the number's owner.</param>
/// <param name="SendWhenNoAccount">A sign-up or enrollment sends its code to a number no account has; a sign-in does not.</param>
/// <param name="Request">Where the request came from.</param>
internal sealed record PhoneCodeIssueRequest(
    string PhoneNumber,
    string Purpose,
    PhoneOtpChallengeContext Context,
    string? UserId,
    bool SendWhenNoAccount,
    SqlOSRequestContext Request);

/// <summary>A code presented against its challenge for <paramref name="Purpose"/>.</summary>
internal sealed record PhoneCodeVerifyRequest(string? ChallengeToken, string? Code, string Purpose, ChallengeBinding Binding);

/// <summary>What issuing a phone-code challenge did.</summary>
internal abstract record PhoneCodeIssue
{
    private PhoneCodeIssue()
    {
    }

    public sealed record Issued(SqlOSPhoneOtpStartResult Result) : PhoneCodeIssue;

    public sealed record Refused(IdentityRefusal Refusal) : PhoneCodeIssue;
}

/// <summary>What checking a phone code did.</summary>
internal abstract record PhoneCodeCheck
{
    private PhoneCodeCheck()
    {
    }

    /// <summary>The provider approved the code: the challenge is spent.</summary>
    public sealed record Verified(SqlOSPhoneOtpChallenge Challenge) : PhoneCodeCheck;

    public sealed record Refused(IdentityRefusal Refusal) : PhoneCodeCheck
    {
        public static Refused InvalidCode { get; } = new(IdentityRefusals.InvalidCode);
    }
}

/// <summary>A typed phone number, normalized to E.164 and allowed by the host's country lists, or why not.</summary>
internal abstract record PhoneNumberCheck
{
    private PhoneNumberCheck()
    {
    }

    public sealed record Valid(PhoneNumber PhoneNumber) : PhoneNumberCheck;

    public sealed record Invalid(IdentityRefusal Refusal) : PhoneNumberCheck;
}
