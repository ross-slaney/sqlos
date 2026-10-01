using Microsoft.EntityFrameworkCore;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;
using SqlOS.Domain.Events;
using SqlOS.Email.Models;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// The step every password-reset process shares once it knows the account: whether the account can
/// be reset, and issuing and emailing its link (<see cref="SqlOSPasswordResetDelivery"/>).
/// </summary>
/// <remarks>
/// <para>
/// A reset link is bound to its account's address (by ID and canonical form) and to the client it
/// was requested from, and goes only to the address stored on the account. A newer link retires the
/// account's open ones.
/// </para>
/// <para>
/// Each change is saved as 7.2.1 committed it: the retired links, then the new link before it is
/// sent, then the send's record. A link that cannot be built or sent is withdrawn with every other
/// open link of the account, the failure is audited (<c>password_reset.email_send_failed</c>), and
/// the failure is rethrown: neither the cleanup nor its audit inherits the request's cancellation,
/// so a caller that disconnects cannot strand a live link.
/// </para>
/// </remarks>
internal sealed class PasswordResetLinks(
    ISqlOSAuthServerDbContext context,
    SqlOSPasswordResetDelivery delivery,
    IAuditRecorder audit)
{
    /// <summary>The 7.x answer when the account cannot be reset.</summary>
    public static readonly IdentityRefusal Unavailable = new("password_reset_unavailable", "Password reset is unavailable for this account.");

    /// <summary>The 7.x answer for an address no account owns.</summary>
    public static readonly IdentityRefusal UnknownEmail = new("unknown_email", "Unknown email address.");

    /// <summary>
    /// True when the account behind <paramref name="email"/> can be reset: local passwords are
    /// enabled, the account is active, and it has a password.
    /// </summary>
    public async Task<bool> IsEligibleAsync(
        SqlOSUserEmail? email,
        SqlOSResolvedCredentialSettings credentialSettings,
        CancellationToken cancellationToken)
    {
        if (!credentialSettings.PasswordEnabled || email?.User == null || !email.User.IsActive)
        {
            return false;
        }

        return await context.Set<SqlOSCredential>()
            .AnyAsync(x => x.UserId == email.UserId && x.Type == "password" && x.RevokedAt == null, cancellationToken);
    }

    /// <summary>The active client <paramref name="clientId"/> names, or null.</summary>
    public async Task<SqlOSClientApplication?> FindActiveClientAsync(string? clientId, CancellationToken cancellationToken)
    {
        var normalized = NormalizeClientKey(clientId);
        if (normalized == null)
        {
            return null;
        }

        return await context.Set<SqlOSClientApplication>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ClientId == normalized && x.IsActive && x.DisabledAt == null, cancellationToken);
    }

    /// <summary>The client key the admission buckets and the audit record: the trimmed client ID, or null.</summary>
    public static string? NormalizeClientKey(string? clientId)
        => string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();

    /// <summary>
    /// Retires the account's open links and issues a new one for <paramref name="email"/>, bound to
    /// the client it was requested from, and saves it.
    /// </summary>
    public async Task<IssuedTemporaryToken> IssueAsync(
        SqlOSUserEmail email,
        string? clientApplicationId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await RetireOpenLinksAsync(email.UserId, now, cancellationToken);

        var kind = SqlOSTemporaryTokenKinds.PasswordReset;
        var link = SqlOSTemporaryToken.Issue(
            kind,
            new PasswordResetPayload(email.Id, email.NormalizedEmail),
            new TemporaryTokenBinding(UserId: email.UserId, ClientApplicationId: clientApplicationId),
            kind.Lifetime.Resolve(delivery.Options.TokenLifetime),
            now);
        context.Set<SqlOSTemporaryToken>().Add(link.Token);
        await context.SaveChangesAsync(cancellationToken);
        return link;
    }

    /// <summary>
    /// Issues a link for <paramref name="email"/> and emails it to that stored address, recording the
    /// send on the link. A failure withdraws the account's open links, is audited and is rethrown.
    /// </summary>
    /// <param name="email">The account's stored address.</param>
    /// <param name="trustedResetUrlTemplate">A template the host passed in process, never one from a public request.</param>
    /// <param name="clientApplicationId">The client the link is bound to.</param>
    /// <param name="clientId">The first-party client the host's URL builder may route by.</param>
    /// <param name="request">Where the request came from (the audit rows' address).</param>
    /// <param name="now">When the link is issued.</param>
    /// <param name="cancellationToken">Cancels the issue and the send; never the cleanup after a failure.</param>
    public async Task<SentPasswordResetLink> SendAsync(
        SqlOSUserEmail email,
        string? trustedResetUrlTemplate,
        string? clientApplicationId,
        string? clientId,
        SqlOSRequestContext request,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var maskedEmail = Masked.Email(email.Email);
        IssuedTemporaryToken? link = null;

        try
        {
            link = await IssueAsync(email, clientApplicationId, now, cancellationToken);
            var expiresAt = now.Add(delivery.Options.TokenLifetime);
            var sent = await delivery.SendAsync(
                email.Email,
                email.UserId,
                link.RawToken,
                expiresAt,
                trustedResetUrlTemplate,
                clientId,
                cancellationToken);

            SqlOSPasswordResetEmailResult result;
            switch (sent)
            {
                case PasswordResetEmailDelivery.HostMessage hostMessage:
                    link.Token.Record(new PasswordResetMessageSent(
                        link.Token.Id,
                        email.UserId,
                        hostMessage.MaskedEmail,
                        request.IpAddress,
                        hostMessage.DeliveryId));
                    result = new SqlOSPasswordResetEmailResult(
                        email.Email,
                        hostMessage.MaskedEmail,
                        expiresAt,
                        hostMessage.DeliveryId,
                        SqlOSEmailDeliveryStatuses.Queued,
                        ProviderMessageId: null,
                        SanitizedError: null,
                        $"Password reset email queued for {hostMessage.MaskedEmail}.");
                    break;
                case PasswordResetEmailDelivery.Template template:
                    link.Token.Record(new PasswordResetEmailSent(
                        link.Token.Id,
                        email.UserId,
                        template.MaskedEmail,
                        request.IpAddress,
                        template.Result.DeliveryId,
                        template.Result.Status,
                        template.Result.ProviderMessageId));
                    result = new SqlOSPasswordResetEmailResult(
                        email.Email,
                        template.MaskedEmail,
                        expiresAt,
                        template.Result.DeliveryId,
                        template.Result.Status,
                        template.Result.ProviderMessageId,
                        template.Result.SanitizedError,
                        $"Password reset email queued for {template.MaskedEmail}.");
                    break;
                default:
                    throw new InvalidOperationException($"Unknown password-reset delivery '{sent.GetType().Name}'.");
            }

            await context.SaveChangesAsync(cancellationToken);
            return new SentPasswordResetLink(link.Token, result);
        }
        catch (Exception exception)
        {
            if (link != null)
            {
                await RetireOpenLinksAsync(email.UserId, now, CancellationToken.None);
            }

            audit.Record(new PasswordResetEmailFailed(email.UserId, maskedEmail, request.IpAddress, exception.Message));
            await context.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Retires every unspent, unexpired reset link of the account: a newer link replaces them.</summary>
    private async Task RetireOpenLinksAsync(string userId, DateTime now, CancellationToken cancellationToken)
    {
        var openLinks = await context.Set<SqlOSTemporaryToken>()
            .Where(SqlOSTemporaryToken.OfKind(SqlOSTemporaryTokenKinds.PasswordReset))
            .Where(SqlOSTemporaryToken.UsableAt(now))
            .Where(x => x.UserId == userId)
            .ToListAsync(cancellationToken);
        if (openLinks.Count == 0)
        {
            return;
        }

        foreach (var openLink in openLinks)
        {
            openLink.Retire(now);
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>A reset link that was emailed: the link, and the 7.x result of the send.</summary>
internal sealed record SentPasswordResetLink(SqlOSTemporaryToken Link, SqlOSPasswordResetEmailResult Result);
