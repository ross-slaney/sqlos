using Microsoft.EntityFrameworkCore;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Policies;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Someone forgot their password and asked for a reset link: the hosted AuthPage, the headless API
/// and the public API all request a reset here.
/// </summary>
/// <remarks>
/// <para>
/// The answer is the same whether or not an account can be reset, and whether or not the email
/// could be sent, so the request reveals nothing about the address. The request is admitted
/// atomically before anything is written (#424): a request over a limit (per address, account, IP
/// address or client) is audited and answered with the time it may be retried. An admitted request
/// leaves a marker and its audit row, then an eligible account is sent a link
/// (<see cref="PasswordResetLinks"/>), only to its stored address.
/// </para>
/// <para>
/// The link is bound to the client the request names when that client is active; a first-party
/// client is also handed to the host's reset-URL builder.
/// </para>
/// </remarks>
internal sealed class RequestPasswordReset(
    ISqlOSAuthServerDbContext context,
    SqlOSSettingsService settings,
    IAdmissionGate admission,
    PasswordResetLinks links,
    SqlOSPasswordResetDelivery delivery,
    IAuditRecorder audit,
    TimeProvider clock)
{
    /// <summary>The generic answer to every admitted request.</summary>
    public const string GenericMessage = "If an account can be reset, you'll receive a password reset email shortly.";

    public async Task<PasswordResetRequestOutcome> ExecuteAsync(RequestPasswordResetCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = clock.GetUtcNow().UtcDateTime;
        var trimmedEmail = command.Email?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedEmail))
        {
            return new PasswordResetRequestOutcome.Refused(IdentityRefusals.EmailRequired);
        }

        var normalizedEmail = SqlOSAdminService.NormalizeEmail(trimmedEmail);
        var maskedEmail = Masked.Email(trimmedEmail);
        var ipAddress = command.Request.IpAddress;
        var client = await links.FindActiveClientAsync(command.ClientId, cancellationToken);
        var clientKey = PasswordResetLinks.NormalizeClientKey(command.ClientId);

        var email = await context.Set<SqlOSUserEmail>()
            .Include(x => x.User)
            .FindByEmailAsync(trimmedEmail, cancellationToken);

        var admitted = await admission.AdmitPasswordResetEmailAsync(
            normalizedEmail,
            email?.UserId,
            AdmissionOrigin.Of(command.Request),
            clientKey,
            now,
            cancellationToken);
        if (!admitted.Admitted)
        {
            var retryAfter = admitted.RetryAfter?.UtcDateTime;
            audit.Record(new PasswordResetSendRateLimited(
                email?.UserId,
                maskedEmail,
                ipAddress,
                admitted.RefusedLimit!,
                retryAfter,
                clientKey));
            await context.SaveChangesAsync(cancellationToken);
            return Answered(trimmedEmail, maskedEmail, now, retryAfter);
        }

        var markerKind = SqlOSTemporaryTokenKinds.PasswordResetRequest;
        var marker = SqlOSTemporaryToken.Issue(
            markerKind,
            new PasswordResetRequestPayload(normalizedEmail, ipAddress, clientKey, "public"),
            new TemporaryTokenBinding(UserId: email?.UserId, ClientApplicationId: client?.Id),
            markerKind.Lifetime.Resolve(delivery.Options.RateLimitWindow),
            now);
        context.Set<SqlOSTemporaryToken>().Add(marker.Token);
        await context.SaveChangesAsync(cancellationToken);

        var credentialSettings = await settings.GetResolvedCredentialSettingsAsync(cancellationToken);
        var eligible = await links.IsEligibleAsync(email, credentialSettings, cancellationToken);
        marker.Token.Record(new PasswordResetRequested(marker.Token.Id, email?.UserId, maskedEmail, ipAddress, eligible, clientKey));
        await context.SaveChangesAsync(cancellationToken);

        if (!eligible || email == null)
        {
            return Answered(trimmedEmail, maskedEmail, now);
        }

        try
        {
            await links.SendAsync(
                email,
                trustedResetUrlTemplate: null,
                client?.Id,
                client?.IsFirstParty == true ? client.ClientId : null,
                command.Request,
                now,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Already withdrawn and audited; the answer stays generic.
        }

        return Answered(trimmedEmail, maskedEmail, now);
    }

    private PasswordResetRequestOutcome Answered(string email, string maskedEmail, DateTime now, DateTime? nextAllowedSendAt = null)
        => new PasswordResetRequestOutcome.Answered(new SqlOSPasswordResetRequestResult(
            email,
            maskedEmail,
            GenericMessage,
            now.Add(delivery.Options.TokenLifetime),
            nextAllowedSendAt ?? now.Add(delivery.Options.ResendCooldown)));
}

/// <summary>A request for a reset link.</summary>
/// <param name="Email">The address as typed.</param>
/// <param name="ClientId">The client the request came from (the authorization request's, on the browser surfaces), or null.</param>
/// <param name="Request">Where the request came from.</param>
internal sealed record RequestPasswordResetCommand(string? Email, string? ClientId, SqlOSRequestContext Request);

/// <summary>What a reset request did.</summary>
internal abstract record PasswordResetRequestOutcome
{
    private PasswordResetRequestOutcome()
    {
    }

    /// <summary>The request was answered generically: a link went out only if an account could be reset.</summary>
    public sealed record Answered(SqlOSPasswordResetRequestResult Result) : PasswordResetRequestOutcome;

    /// <summary>The request named no address.</summary>
    public sealed record Refused(IdentityRefusal Refusal) : PasswordResetRequestOutcome;
}

/// <summary>
/// Emails a reset link to a known account at the request of trusted code: the host
/// (<see cref="SqlOSAuthService.SendPasswordResetEmailAsync"/>, by address, with a reset-URL template
/// it trusts) or an operator (the admin API and the dashboard, by user).
/// </summary>
/// <remarks>
/// Unlike a forgotten-password request, the caller is told when the account is unknown, has no
/// address, or cannot be reset, and a failed send throws after the link is withdrawn and the failure
/// audited. An operator's send is audited as such (<c>password_reset.admin_email_sent</c>); its link
/// is bound to no client.
/// </remarks>
internal sealed class SendPasswordResetEmail(
    ISqlOSAuthServerDbContext context,
    SqlOSSettingsService settings,
    PasswordResetLinks links,
    TimeProvider clock)
{
    public static readonly IdentityRefusal UserIdRequired = new("user_id_required", "User id is required.");
    public static readonly IdentityRefusal UserNotFound = new("user_not_found", "User not found.");
    public static readonly IdentityRefusal UserHasNoEmail = new("user_has_no_email", "User does not have an email address.");

    public async Task<PasswordResetEmailOutcome> ExecuteAsync(SendPasswordResetEmailCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = clock.GetUtcNow().UtcDateTime;
        SqlOSUserEmail email;
        switch (command.Recipient)
        {
            case PasswordResetRecipient.Address address:
                var addressed = await context.Set<SqlOSUserEmail>()
                    .Include(x => x.User)
                    .FindByEmailAsync(address.Email, cancellationToken);
                if (addressed == null)
                {
                    return new PasswordResetEmailOutcome.Refused(PasswordResetLinks.UnknownEmail);
                }

                email = addressed;
                break;
            case PasswordResetRecipient.User user:
                var userId = user.UserId?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(userId))
                {
                    return new PasswordResetEmailOutcome.Refused(UserIdRequired);
                }

                var account = await context.Set<SqlOSUser>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);
                if (account == null)
                {
                    return new PasswordResetEmailOutcome.Refused(UserNotFound);
                }

                // The account's default address, else its verified address first, the oldest first.
                var stored = await context.Set<SqlOSUserEmail>()
                        .Include(x => x.User)
                        .FirstOrDefaultAsync(x => x.UserId == account.Id && x.Email == account.DefaultEmail, cancellationToken)
                    ?? await context.Set<SqlOSUserEmail>()
                        .Include(x => x.User)
                        .OrderByDescending(x => x.IsVerified)
                        .ThenBy(x => x.CreatedAt)
                        .FirstOrDefaultAsync(x => x.UserId == account.Id, cancellationToken);
                if (stored == null)
                {
                    return new PasswordResetEmailOutcome.Refused(UserHasNoEmail);
                }

                email = stored;
                break;
            default:
                throw new InvalidOperationException($"Unknown password-reset recipient '{command.Recipient.GetType().Name}'.");
        }

        var credentialSettings = await settings.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!await links.IsEligibleAsync(email, credentialSettings, cancellationToken))
        {
            return new PasswordResetEmailOutcome.Refused(PasswordResetLinks.Unavailable);
        }

        if (command.Recipient is PasswordResetRecipient.User)
        {
            var sentByOperator = await links.SendAsync(
                email,
                command.ResetUrlTemplate,
                clientApplicationId: null,
                clientId: null,
                command.Request,
                now,
                cancellationToken);
            sentByOperator.Link.Record(new PasswordResetEmailSentByOperator(
                sentByOperator.Link.Id,
                email.UserId,
                sentByOperator.Result.MaskedEmail,
                command.Request.IpAddress,
                sentByOperator.Result.DeliveryId,
                sentByOperator.Result.DeliveryStatus));
            await context.SaveChangesAsync(cancellationToken);
            return new PasswordResetEmailOutcome.Sent(sentByOperator.Result);
        }

        var client = await links.FindActiveClientAsync(command.ClientId, cancellationToken);
        var sent = await links.SendAsync(
            email,
            command.ResetUrlTemplate,
            client?.Id,
            client?.IsFirstParty == true ? client.ClientId : null,
            command.Request,
            now,
            cancellationToken);
        return new PasswordResetEmailOutcome.Sent(sent.Result);
    }
}

/// <summary>A reset email trusted code sends.</summary>
/// <param name="Recipient">The account: by address (the host) or by user (an operator).</param>
/// <param name="ResetUrlTemplate">A reset-URL template the caller trusts, or null for the host's builder or the hosted reset page.</param>
/// <param name="ClientId">The client the host binds the link to (an operator's link has none).</param>
/// <param name="Request">Where the request came from.</param>
internal sealed record SendPasswordResetEmailCommand(
    PasswordResetRecipient Recipient,
    string? ResetUrlTemplate,
    string? ClientId,
    SqlOSRequestContext Request);

/// <summary>Whose reset email trusted code sends.</summary>
internal abstract record PasswordResetRecipient
{
    private PasswordResetRecipient()
    {
    }

    /// <summary>The account that owns <see cref="Email"/> (the host's in-process send).</summary>
    public sealed record Address(string Email) : PasswordResetRecipient;

    /// <summary>The account <see cref="UserId"/>, at its stored address (an operator's send).</summary>
    public sealed record User(string? UserId) : PasswordResetRecipient;
}

/// <summary>What a trusted reset send did.</summary>
internal abstract record PasswordResetEmailOutcome
{
    private PasswordResetEmailOutcome()
    {
    }

    public sealed record Sent(SqlOSPasswordResetEmailResult Result) : PasswordResetEmailOutcome;

    public sealed record Refused(IdentityRefusal Refusal) : PasswordResetEmailOutcome;
}

/// <summary>
/// Issues a reset link for a known account and hands its raw token to trusted host code, which
/// delivers it itself (<see cref="SqlOSAuthService.CreatePasswordResetTokenAsync"/>). The account must
/// be resettable; a newer link retires its open ones.
/// </summary>
internal sealed class IssuePasswordResetToken(
    ISqlOSAuthServerDbContext context,
    SqlOSSettingsService settings,
    PasswordResetLinks links,
    TimeProvider clock)
{
    public async Task<PasswordResetTokenOutcome> ExecuteAsync(IssuePasswordResetTokenCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var email = await context.Set<SqlOSUserEmail>()
            .Include(x => x.User)
            .FindByEmailAsync(command.Email, cancellationToken);
        if (email == null)
        {
            return new PasswordResetTokenOutcome.Refused(PasswordResetLinks.UnknownEmail);
        }

        var credentialSettings = await settings.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!await links.IsEligibleAsync(email, credentialSettings, cancellationToken))
        {
            return new PasswordResetTokenOutcome.Refused(PasswordResetLinks.Unavailable);
        }

        var client = await links.FindActiveClientAsync(command.ClientId, cancellationToken);
        var link = await links.IssueAsync(email, client?.Id, clock.GetUtcNow().UtcDateTime, cancellationToken);
        return new PasswordResetTokenOutcome.Issued(link.RawToken);
    }
}

/// <summary>A reset token trusted host code asks for.</summary>
internal sealed record IssuePasswordResetTokenCommand(string Email, string? ClientId);

/// <summary>What issuing a reset token did.</summary>
internal abstract record PasswordResetTokenOutcome
{
    private PasswordResetTokenOutcome()
    {
    }

    /// <summary>The raw token, handed out once.</summary>
    public sealed record Issued(string RawToken) : PasswordResetTokenOutcome;

    public sealed record Refused(IdentityRefusal Refusal) : PasswordResetTokenOutcome;
}

/// <summary>
/// Sets a new password with a reset link: the hosted reset page, the headless API and the public
/// API all reset here.
/// </summary>
/// <remarks>
/// <para>
/// The password policy is asked before the link is looked at, so a refused password leaves the link
/// usable (#417, BL-0006). The link is spent once, on its own: a replay, an unknown or expired link,
/// or one whose account is missing, inactive or has no password is refused and audited, and stays
/// spent. Local passwords must be enabled.
/// </para>
/// <para>
/// The link was mailed to an address of the account, so completing it proves that mailbox: an
/// unverified address is claimed, evicting what was attached before it except the password being
/// reset (#420, #422). Every session and token of the account is revoked, by the claim or, when
/// nothing is claimed, directly. The new password, the claim and the revocations commit together;
/// the completion's audit row follows.
/// </para>
/// </remarks>
internal sealed class ResetPassword(
    ISqlOSAuthServerDbContext context,
    SqlOSSettingsService settings,
    IAuditRecorder audit,
    TimeProvider clock)
{
    public static readonly IdentityRefusal LinkInvalid = new("reset_link_invalid", "Password reset token is invalid or expired.");

    /// <summary>The reason a reset records on the sessions it revokes.</summary>
    public const string SessionRevocationReason = "password_reset";

    public async Task<PasswordResetOutcome> ExecuteAsync(ResetPasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var credentialSettings = await settings.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!credentialSettings.PasswordEnabled)
        {
            return new PasswordResetOutcome.Refused(IdentityRefusals.PasswordLoginDisabled);
        }

        // The policy decides before the link is spent, so a refused password leaves the link usable.
        if (PasswordPolicy.Default.Check(command.NewPassword) is { } refusal)
        {
            return new PasswordResetOutcome.Refused(new IdentityRefusal($"password_{refusal.Rule}", refusal.Message));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var link = await PresentedTokens.SpendAsync(context, SqlOSTemporaryTokenKinds.PasswordReset, command.Token, now, cancellationToken);
        if (link == null)
        {
            return await RefuseAsync(userId: null, "missing_or_consumed", cancellationToken);
        }

        var user = await context.FindUserAsync(link.UserId, SqlOSUserParts.Emails | SqlOSUserParts.Credentials, cancellationToken);
        if (user == null || !user.IsActive)
        {
            return await RefuseAsync(link.UserId, user == null ? "missing_user" : "inactive_user", cancellationToken);
        }

        var credential = user.Password;
        if (credential == null)
        {
            return await RefuseAsync(link.UserId, "missing_password_credential", cancellationToken);
        }

        var claim = EmailClaimOutcome.NotClaimed;
        var payload = link.ReadPayload(SqlOSTemporaryTokenKinds.PasswordReset);
        var resetEmail = user.FindEmail(payload?.EmailId);
        if (resetEmail != null && SqlOSEmailAddress.MatchesStoredEmail(resetEmail, payload!.NormalizedEmail))
        {
            // The reset link went to this address, so completing it proves the mailbox. An
            // unverified address is claimed: everything attached before the owner proved it is
            // evicted except the password being reset now.
            claim = await ClaimEmailOwnership.StageAsync(
                context,
                user,
                new OwnershipProof(EmailAddress.Parse(resetEmail.Email), OwnershipProofMethod.PasswordReset),
                PresentedCredentials.PasswordBeingReset(credential.Id),
                now,
                cancellationToken,
                sessionRevocationReason: SessionRevocationReason);
        }

        user.SetPassword(command.NewPassword, PasswordPolicy.Default, now);
        if (!claim.Claimed)
        {
            await SqlOSAuthLifecyclePolicy.RevokeAsync(
                context,
                user.Id,
                organizationId: null,
                SessionRevocationReason,
                now,
                cancellationToken: cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);

        link.Record(new PasswordResetCompleted(link.Id, user.Id));
        await context.SaveChangesAsync(cancellationToken);
        return new PasswordResetOutcome.Reset(user);
    }

    private async Task<PasswordResetOutcome> RefuseAsync(string? userId, string reason, CancellationToken cancellationToken)
    {
        audit.Record(new PasswordResetLinkRefused(userId, reason));
        await context.SaveChangesAsync(cancellationToken);
        return new PasswordResetOutcome.Refused(LinkInvalid);
    }
}

/// <summary>A new password presented with a reset link.</summary>
internal sealed record ResetPasswordCommand(string Token, string NewPassword);

/// <summary>What a reset did.</summary>
internal abstract record PasswordResetOutcome
{
    private PasswordResetOutcome()
    {
    }

    /// <summary>The account's password was replaced and its sessions revoked.</summary>
    public sealed record Reset(SqlOSUser User) : PasswordResetOutcome;

    public sealed record Refused(IdentityRefusal Refusal) : PasswordResetOutcome;
}
