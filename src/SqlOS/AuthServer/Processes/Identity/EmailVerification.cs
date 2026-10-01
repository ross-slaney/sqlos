using Microsoft.EntityFrameworkCore;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Someone asked for a verification email for an address: the public API's verification routes
/// request one here.
/// </summary>
/// <remarks>
/// <para>
/// The answer is the same whatever the address is, and the request is always audited. Only an
/// address an account owns unverified is sent a link, to that stored address, and not again while
/// a link sent for it in the last minute is still open. Creating the link is audited
/// (<c>user.email-verification-token-created</c>).
/// </para>
/// <para>
/// A link that cannot be sent is withdrawn and the failure audited; the answer stays generic.
/// Neither the withdrawal nor its audit inherits the request's cancellation.
/// </para>
/// </remarks>
internal sealed class RequestEmailVerification(
    ISqlOSAuthServerDbContext context,
    SqlOSEmailVerificationDelivery delivery,
    IAuditRecorder audit,
    TimeProvider clock)
{
    /// <summary>The generic answer to every request.</summary>
    public const string GenericMessage = "If the email can be verified, you'll receive a verification email shortly.";

    /// <summary>How long after a link was sent for an address no other is sent.</summary>
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromMinutes(1);

    public async Task<EmailVerificationRequestOutcome> ExecuteAsync(RequestEmailVerificationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var trimmedEmail = command.Email?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedEmail))
        {
            return new EmailVerificationRequestOutcome.Refused(IdentityRefusals.EmailRequired);
        }

        var maskedEmail = Masked.Email(trimmedEmail);
        var ipAddress = command.Request.IpAddress;
        var now = clock.GetUtcNow().UtcDateTime;
        var email = await context.Set<SqlOSUserEmail>().FindByEmailAsync(trimmedEmail, cancellationToken);

        audit.Record(new EmailVerificationRequested(email?.UserId, maskedEmail, ipAddress, email is { IsVerified: false }));
        await context.SaveChangesAsync(cancellationToken);

        if (email == null || email.IsVerified)
        {
            return Answered;
        }

        var kind = SqlOSTemporaryTokenKinds.EmailVerification;
        var recentLinks = await context.Set<SqlOSTemporaryToken>()
            .Where(SqlOSTemporaryToken.OfKind(kind))
            .Where(SqlOSTemporaryToken.UsableAt(now))
            .Where(x => x.UserId == email.UserId && x.CreatedAt >= now.Subtract(ResendCooldown))
            .ToListAsync(cancellationToken);
        if (recentLinks.Any(link => link.ReadPayload(kind)?.EmailId == email.Id))
        {
            return Answered;
        }

        IssuedTemporaryToken? link = null;
        try
        {
            var issued = EmailVerificationLinks.Issue(email, now);
            context.Set<SqlOSTemporaryToken>().Add(issued.Token);
            await context.SaveChangesAsync(cancellationToken);
            link = issued;

            var sent = await delivery.SendAsync(email.Email, email.Id, issued.RawToken, cancellationToken);
            issued.Token.Record(new EmailVerificationEmailSent(
                issued.Token.Id,
                email.UserId,
                maskedEmail,
                ipAddress,
                sent.DeliveryId,
                sent.Status,
                sent.ProviderMessageId));
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (link != null && link.Token.IsUsable(now))
            {
                link.Token.Retire(now);
                await context.SaveChangesAsync(CancellationToken.None);
            }

            audit.Record(new EmailVerificationEmailFailed(email.UserId, maskedEmail, ipAddress, exception.Message));
            await context.SaveChangesAsync(CancellationToken.None);
        }

        return Answered;
    }

    private static EmailVerificationRequestOutcome Answered { get; } =
        new EmailVerificationRequestOutcome.Answered(new SqlOSEmailVerificationRequestResult(GenericMessage));
}

/// <summary>A request for a verification email.</summary>
internal sealed record RequestEmailVerificationCommand(string? Email, SqlOSRequestContext Request);

/// <summary>What a verification request did.</summary>
internal abstract record EmailVerificationRequestOutcome
{
    private EmailVerificationRequestOutcome()
    {
    }

    /// <summary>The request was answered generically: a link went out only for an address an account owns unverified.</summary>
    public sealed record Answered(SqlOSEmailVerificationRequestResult Result) : EmailVerificationRequestOutcome;

    /// <summary>The request named no address.</summary>
    public sealed record Refused(IdentityRefusal Refusal) : EmailVerificationRequestOutcome;
}

/// <summary>
/// Issues a verification link for an address an account owns and hands its raw token to trusted
/// host code, which delivers it itself (<see cref="SqlOSAuthService.CreateEmailVerificationTokenAsync"/>).
/// </summary>
internal sealed class IssueEmailVerificationToken(ISqlOSAuthServerDbContext context, TimeProvider clock)
{
    public static readonly IdentityRefusal UnknownEmail = new("unknown_email", "Unknown email address.");

    public async Task<EmailVerificationTokenOutcome> ExecuteAsync(IssueEmailVerificationTokenCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var email = await context.Set<SqlOSUserEmail>().FindByEmailAsync(command.Email, cancellationToken);
        if (email == null)
        {
            return new EmailVerificationTokenOutcome.Refused(UnknownEmail);
        }

        var link = EmailVerificationLinks.Issue(email, clock.GetUtcNow().UtcDateTime);
        context.Set<SqlOSTemporaryToken>().Add(link.Token);
        await context.SaveChangesAsync(cancellationToken);
        return new EmailVerificationTokenOutcome.Issued(link.RawToken);
    }
}

/// <summary>A verification token trusted host code asks for.</summary>
internal sealed record IssueEmailVerificationTokenCommand(string Email);

/// <summary>What issuing a verification token did.</summary>
internal abstract record EmailVerificationTokenOutcome
{
    private EmailVerificationTokenOutcome()
    {
    }

    /// <summary>The raw token, handed out once.</summary>
    public sealed record Issued(string RawToken) : EmailVerificationTokenOutcome;

    public sealed record Refused(IdentityRefusal Refusal) : EmailVerificationTokenOutcome;
}

/// <summary>
/// The owner opened the verification link: the public API's verification routes and the hosted
/// verification page verify here.
/// </summary>
/// <remarks>
/// The link is spent once, on its own. It was mailed to one address of the account and confirms
/// that address, which becomes verified and the account's default email
/// (<see cref="SqlOSUser.VerifyEmail"/>, <c>user.email-verified</c>). It is the account's
/// confirmation step, not a sign-in: it claims nothing and revokes nothing.
/// </remarks>
internal sealed class VerifyEmail(ISqlOSAuthServerDbContext context, TimeProvider clock)
{
    public static readonly IdentityRefusal LinkInvalid = new("verification_link_invalid", "Email verification token is invalid or expired.");
    public static readonly IdentityRefusal PayloadInvalid = new("verification_link_payload_invalid", "Email verification token payload is invalid.");

    public async Task<EmailVerificationOutcome> ExecuteAsync(VerifyEmailCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = clock.GetUtcNow().UtcDateTime;
        var link = await PresentedTokens.SpendAsync(context, SqlOSTemporaryTokenKinds.EmailVerification, command.Token, now, cancellationToken);
        if (link == null)
        {
            return new EmailVerificationOutcome.Refused(LinkInvalid);
        }

        var payload = link.ReadPayload(SqlOSTemporaryTokenKinds.EmailVerification);
        if (payload == null)
        {
            return new EmailVerificationOutcome.Refused(PayloadInvalid);
        }

        // A link whose address is gone fails as 7.x did, with the query's own error.
        var email = await context.Set<SqlOSUserEmail>().AsNoTracking().FirstAsync(x => x.Id == payload.EmailId, cancellationToken);
        var user = await context.GetUserAsync(email.UserId, SqlOSUserParts.Emails, cancellationToken);
        user.VerifyEmail(new OwnershipProof(EmailAddress.Parse(email.Email), OwnershipProofMethod.EmailVerification), now);
        await context.SaveChangesAsync(cancellationToken);
        return new EmailVerificationOutcome.Verified(user);
    }
}

/// <summary>A verification link presented by its owner.</summary>
internal sealed record VerifyEmailCommand(string Token);

/// <summary>What opening a verification link did.</summary>
internal abstract record EmailVerificationOutcome
{
    private EmailVerificationOutcome()
    {
    }

    /// <summary>The address is verified and the account's default email.</summary>
    public sealed record Verified(SqlOSUser User) : EmailVerificationOutcome;

    public sealed record Refused(IdentityRefusal Refusal) : EmailVerificationOutcome;
}

/// <summary>Verification links: one per address, bound to its account, living a day.</summary>
internal static class EmailVerificationLinks
{
    /// <summary>A new verification link for <paramref name="email"/>, not yet saved.</summary>
    public static IssuedTemporaryToken Issue(SqlOSUserEmail email, DateTime now)
    {
        var kind = SqlOSTemporaryTokenKinds.EmailVerification;
        return SqlOSTemporaryToken.Issue(
            kind,
            new EmailVerificationPayload(email.Id),
            new TemporaryTokenBinding(UserId: email.UserId),
            kind.Lifetime.Resolve(configured: null),
            now);
    }
}
