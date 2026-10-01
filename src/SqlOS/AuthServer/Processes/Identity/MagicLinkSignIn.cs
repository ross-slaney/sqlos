using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Sends a sign-in link to an address: the hosted AuthPage, the headless API and the public API
/// all start a sign-in link here.
/// </summary>
/// <remarks>
/// <para>
/// The send is admitted atomically before anything is written (#424), and a resend the cooldown
/// refuses gives its admission back. A newer link retires the unspent links of the same address and
/// context. The answer never reveals whether an account exists: a link is issued for every valid
/// address, but sent only when an active account owns it, and only to the address stored on that
/// account (#422).
/// </para>
/// <para>
/// A direct login is first-party only (#419). An authorization request remembers the address as its
/// login hint.
/// </para>
/// </remarks>
internal sealed class StartMagicLinkSignIn(
    ISqlOSAuthServerDbContext context,
    SqlOSSettingsService settings,
    SqlOSAdminService admin,
    SqlOSMagicLinkService links,
    SqlOSSignInLinkDelivery delivery,
    TimeProvider clock)
{
    public async Task<SignInLinkStartOutcome> ExecuteAsync(StartMagicLinkSignInCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!(await settings.GetResolvedCredentialSettingsAsync(cancellationToken)).MagicLinkEnabled)
        {
            return new SignInLinkStartOutcome.Refused(IdentityRefusals.SignInLinksUnavailable);
        }

        string? authorizationRequestId = null;
        string? clientApplicationId = null;
        string? requestedOrganizationId = null;
        switch (command.Target)
        {
            case LoginTarget.AuthorizationRequest request:
                request.Request.LoginHintEmail = command.Email.Trim();
                await context.SaveChangesAsync(cancellationToken);
                authorizationRequestId = request.Request.Id;
                clientApplicationId = request.Request.ClientApplicationId;
                break;
            case LoginTarget.DirectLogin directLogin:
                var client = await admin.RequireClientAsync(directLogin.ClientId, cancellationToken);
                await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(admin, client, command.Request, userId: null, cancellationToken);
                clientApplicationId = client.Id;
                requestedOrganizationId = directLogin.OrganizationId;
                break;
        }

        if (EmailCodeChallenges.ParseAddress(command.Email) is not { } typedAddress)
        {
            return new SignInLinkStartOutcome.Refused(EmailCodeChallenges.InvalidAddress(command.Email));
        }

        var options = links.Options;
        var now = clock.GetUtcNow().UtcDateTime;
        var normalizedEmail = typedAddress.Canonical;
        var origin = AdmissionOrigin.Of(command.Request);
        var ipAddress = origin.IpAddress;
        var maskedEmail = Masked.Email(typedAddress.Address);

        // The send is admitted atomically before anything is written, so requests sent together
        // can never exceed a limit between them (#424).
        var admission = await links.Admission.AdmitSignInLinkAsync(typedAddress, origin, clientApplicationId, now, cancellationToken);
        if (!admission.Admitted)
        {
            links.AuditRecorder.Record(new MagicLinkSendRateLimited(maskedEmail, ipAddress, admission.RefusedLimit!, clientApplicationId, requestedOrganizationId));
            await context.SaveChangesAsync(cancellationToken);
            return new SignInLinkStartOutcome.Refused(new IdentityRefusal("rate_limited", "Too many sign-in link requests. Try again later."));
        }

        // Only this client's recent links can be in the same sign-in context.
        var recent = (await context.Set<SqlOSTemporaryToken>()
                .Where(SqlOSTemporaryToken.OfKind(SqlOSTemporaryTokenKinds.MagicLink))
                .Where(x => x.ClientApplicationId == clientApplicationId && x.CreatedAt >= now.Subtract(options.RateLimitWindow))
                .ToListAsync(cancellationToken))
            .Select(token => (Token: token, Payload: token.ReadPayload(SqlOSTemporaryTokenKinds.MagicLink)))
            .Where(x => x.Payload != null)
            .ToArray();
        var inThisContext = recent
            .Where(x => x.Token.ConsumedAt == null
                && x.Token.ExpiresAt > now
                && string.Equals(x.Payload!.NormalizedEmail, normalizedEmail, StringComparison.Ordinal)
                && string.Equals(x.Payload.AuthorizationRequestId, authorizationRequestId, StringComparison.Ordinal)
                && string.Equals(x.Token.ClientApplicationId, clientApplicationId, StringComparison.Ordinal)
                && string.Equals(x.Payload.RequestedOrganizationId, requestedOrganizationId, StringComparison.Ordinal))
            .ToArray();
        var latestContextToken = inThisContext.OrderByDescending(x => x.Token.CreatedAt).FirstOrDefault();
        if (latestContextToken.Token != null && latestContextToken.Token.CreatedAt > now.Subtract(options.ResendCooldown))
        {
            // A resend the cooldown refuses sends nothing, so it does not count against a limit.
            await links.Admission.WithdrawAsync(admission, now, cancellationToken);
            return new SignInLinkStartOutcome.Refused(new IdentityRefusal(
                "resend_cooldown",
                $"Wait {(int)Math.Ceiling(options.ResendCooldown.TotalSeconds)} seconds before requesting another sign-in link."));
        }

        foreach (var activeToken in inThisContext)
        {
            activeToken.Token.Retire(now);
        }

        var emailRecord = await context.Set<SqlOSUserEmail>()
            .Include(x => x.User)
            .FindByNormalizedEmailAsync(normalizedEmail, command.Email, cancellationToken);
        var shouldSend = emailRecord?.User != null && emailRecord.User.IsActive;
        var expiresAt = now.Add(options.TokenLifetime);

        // A link for an existing account is only ever delivered to the address stored on that
        // account, never to the typed spelling.
        var payload = new MagicLinkPayload(
            emailRecord?.Email.Trim() ?? typedAddress.Address,
            normalizedEmail,
            maskedEmail,
            emailRecord?.Id,
            authorizationRequestId,
            clientApplicationId,
            requestedOrganizationId,
            ipAddress,
            command.Request.UserAgent,
            shouldSend);
        var link = SqlOSTemporaryToken.Issue(
            SqlOSTemporaryTokenKinds.MagicLink,
            payload,
            new TemporaryTokenBinding(emailRecord?.UserId, clientApplicationId, requestedOrganizationId),
            SqlOSTemporaryTokenKinds.MagicLink.Lifetime.Resolve(options.TokenLifetime),
            now);
        context.Set<SqlOSTemporaryToken>().Add(link.Token);
        await context.SaveChangesAsync(cancellationToken);

        if (shouldSend)
        {
            try
            {
                await delivery.SendAsync(payload, link.RawToken, expiresAt, cancellationToken);
            }
            catch
            {
                link.Token.Retire(now);
                link.Token.Record(new MagicLinkDeliveryFailed(
                    link.Token.Id,
                    maskedEmail,
                    ipAddress,
                    clientApplicationId,
                    authorizationRequestId,
                    requestedOrganizationId));
                await context.SaveChangesAsync(cancellationToken);
                return new SignInLinkStartOutcome.Refused(new IdentityRefusal("delivery_failed", "We couldn't send a sign-in link right now."));
            }
        }

        link.Token.Record(new MagicLinkRequested(
            link.Token.Id,
            maskedEmail,
            ipAddress,
            clientApplicationId,
            authorizationRequestId,
            requestedOrganizationId,
            shouldSend));
        await context.SaveChangesAsync(cancellationToken);

        return new SignInLinkStartOutcome.Sent(new SqlOSMagicLinkStartResult(
            typedAddress.Address,
            maskedEmail,
            $"If an account exists for {maskedEmail}, check your email for a sign-in link.",
            expiresAt,
            now.Add(options.ResendCooldown)));
    }
}

/// <summary>A sign-in link to send.</summary>
/// <param name="Email">The address, after the surface applied the invitation it carries.</param>
/// <param name="Target">Where the link will complete: it binds the link to the authorization request or the client.</param>
/// <param name="Request">Where the request came from.</param>
internal sealed record StartMagicLinkSignInCommand(string Email, LoginTarget Target, SqlOSRequestContext Request);

/// <summary>What starting a sign-in link did.</summary>
internal abstract record SignInLinkStartOutcome
{
    private SignInLinkStartOutcome()
    {
    }

    public sealed record Sent(SqlOSMagicLinkStartResult Result) : SignInLinkStartOutcome;

    public sealed record Refused(IdentityRefusal Refusal) : SignInLinkStartOutcome;
}

/// <summary>
/// Signs a person in with the link sent to their address: one implementation for the hosted
/// AuthPage, the headless API and the public API, each of which completes the link where it was
/// started (<see cref="SignInLinkTarget"/>).
/// </summary>
/// <remarks>
/// A link is spent once: a replay, or a link that never existed, is refused and audited. It signs
/// in only while the exact address it was delivered to still belongs to its account, and proves
/// that mailbox: an unverified address is claimed, evicting what was attached before (#420, #422),
/// and becomes the account's default email. The link is spent before anything else is decided, so
/// a refusal after that point still uses it up, as in 7.2.1.
/// </remarks>
internal sealed class CompleteMagicLinkSignIn(
    ISqlOSAuthServerDbContext context,
    SqlOSSettingsService settings,
    SqlOSMagicLinkService links,
    SqlOSInvitationService? invitations,
    ILoginCompletion completion,
    TimeProvider clock)
{
    public async Task<SignInLinkOutcome> ExecuteAsync(CompleteMagicLinkSignInCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!(await settings.GetResolvedCredentialSettingsAsync(cancellationToken)).MagicLinkEnabled)
        {
            return new SignInLinkOutcome.Refused(IdentityRefusals.SignInLinksUnavailable);
        }

        var rawToken = command.Token?.Trim();
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return new SignInLinkOutcome.Refused(IdentityRefusals.InvalidLink);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var token = await FindUsableAsync(rawToken, now, cancellationToken);
        if (token == null)
        {
            links.AuditRecorder.Record(new MagicLinkNotFound());
            await context.SaveChangesAsync(cancellationToken);
            return new SignInLinkOutcome.Refused(IdentityRefusals.InvalidLink);
        }

        var payload = token.ReadPayload(SqlOSTemporaryTokenKinds.MagicLink);
        if (payload == null || !IsBoundAsExpected(token, payload, command.Target))
        {
            return new SignInLinkOutcome.Refused(IdentityRefusals.InvalidLink);
        }

        // The link is spent first: a concurrent completion that spent it is a replay.
        token.Consume(SqlOSTemporaryTokenKinds.MagicLink, now);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            foreach (var entry in exception.Entries)
            {
                entry.State = EntityState.Detached;
            }

            links.AuditRecorder.Record(new MagicLinkReplayed(
                payload.MaskedEmail,
                payload.IpAddress,
                payload.ClientApplicationId,
                payload.AuthorizationRequestId));
            await context.SaveChangesAsync(cancellationToken);
            return new SignInLinkOutcome.Refused(IdentityRefusals.InvalidLink);
        }

        var user = await context.FindUserAsync(token.UserId, SqlOSUserParts.Emails, cancellationToken);
        if (user is not { IsActive: true })
        {
            return new SignInLinkOutcome.Refused(IdentityRefusals.InvalidLink);
        }

        // The link was delivered to one stored address; it signs in only while that exact
        // address still belongs to this account.
        var userEmail = string.IsNullOrWhiteSpace(payload.UserEmailId) ? null : user.FindEmail(payload.UserEmailId);
        if (userEmail == null || !SqlOSEmailAddress.MatchesStoredEmail(userEmail, payload.NormalizedEmail))
        {
            return new SignInLinkOutcome.Refused(IdentityRefusals.InvalidLink);
        }

        // The link proved the mailbox it was delivered to. An unverified address is claimed:
        // whatever was attached before the owner proved it is evicted in this same save, and the
        // address becomes the account's default email.
        var ownership = new OwnershipProof(EmailAddress.Parse(payload.Email), OwnershipProofMethod.MagicLink);
        await ClaimEmailOwnership.StageAsync(context, user, ownership, PresentedCredentials.None, now, cancellationToken);
        user.MakeDefaultEmail(ownership, now);
        token.Record(new MagicLinkCompleted(
            token.Id,
            payload.MaskedEmail,
            payload.IpAddress,
            user.Id,
            payload.ClientApplicationId,
            payload.AuthorizationRequestId,
            payload.RequestedOrganizationId));
        await context.SaveChangesAsync(cancellationToken);

        var destination = await ResolveDestinationAsync(token, payload, command.Target, now, cancellationToken);
        if (destination is DestinationRefused refused)
        {
            return new SignInLinkOutcome.Refused(refused.Refusal);
        }

        var evidence = new LoginEvidence(user, [AuthenticationMethods.MagicLink], [ownership], now);
        var resolved = ((DestinationResolved)destination).Destination;
        return new SignInLinkOutcome.SignedIn(
            evidence,
            await completion.CompleteAsync(evidence, resolved, cancellationToken),
            resolved,
            payload.Email);
    }

    private Task<SqlOSTemporaryToken?> FindUsableAsync(string rawToken, DateTime now, CancellationToken cancellationToken)
        => context.Set<SqlOSTemporaryToken>()
            .Where(SqlOSTemporaryToken.OfKind(SqlOSTemporaryTokenKinds.MagicLink))
            .Where(SqlOSTemporaryToken.Presented(rawToken))
            .Where(SqlOSTemporaryToken.UsableAt(now))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// A link completes only in the context it was sent for: its client and organization as issued,
    /// and, where the surface names one, its authorization request.
    /// </summary>
    private static bool IsBoundAsExpected(SqlOSTemporaryToken token, MagicLinkPayload payload, SignInLinkTarget target)
    {
        if (!string.Equals(token.ClientApplicationId, payload.ClientApplicationId, StringComparison.Ordinal)
            || !string.Equals(token.OrganizationId, payload.RequestedOrganizationId, StringComparison.Ordinal))
        {
            return false;
        }

        var (expectedAuthorizationRequestId, requireMatch) = target switch
        {
            SignInLinkTarget.Headless headless => (headless.RequestId, !string.IsNullOrWhiteSpace(headless.RequestId)),
            SignInLinkTarget.DirectLogin => (null, true),
            _ => ((string?)null, false)
        };
        if (!requireMatch)
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(expectedAuthorizationRequestId)
            ? string.IsNullOrWhiteSpace(payload.AuthorizationRequestId)
            : string.Equals(payload.AuthorizationRequestId, expectedAuthorizationRequestId, StringComparison.Ordinal);
    }

    /// <summary>Where the spent link completes, as the surface that received it decides.</summary>
    private async Task<DestinationResolution> ResolveDestinationAsync(
        SqlOSTemporaryToken token,
        MagicLinkPayload payload,
        SignInLinkTarget target,
        DateTime now,
        CancellationToken cancellationToken)
    {
        switch (target)
        {
            case SignInLinkTarget.HostedPage:
                if (string.IsNullOrWhiteSpace(payload.AuthorizationRequestId))
                {
                    return new DestinationResolved(new LoginDestination.Browser(InvitationToken: null, InSignupTransaction: false));
                }

                var hostedRequest = await FindActiveAuthorizationRequestAsync(payload.AuthorizationRequestId, now, cancellationToken);
                return hostedRequest == null
                    || !string.Equals(hostedRequest.ClientApplicationId, token.ClientApplicationId, StringComparison.Ordinal)
                    ? new DestinationRefused(IdentityRefusals.InvalidLink)
                    : new DestinationResolved(new LoginDestination.AuthorizationRequest(hostedRequest));

            case SignInLinkTarget.Headless headless:
                var authorizationRequestId = payload.AuthorizationRequestId ?? headless.RequestId;
                if (string.IsNullOrWhiteSpace(authorizationRequestId))
                {
                    return new DestinationRefused(IdentityRefusals.InvalidLink);
                }

                var request = await FindActiveAuthorizationRequestAsync(authorizationRequestId, now, cancellationToken)
                    ?? throw new InvalidOperationException("Authorization request is invalid or expired.");
                if (!string.IsNullOrWhiteSpace(headless.InvitationToken) && invitations != null)
                {
                    await invitations.BindInvitationToAuthorizationRequestAsync(headless.InvitationToken, request, cancellationToken);
                }

                return string.Equals(payload.AuthorizationRequestId, request.Id, StringComparison.Ordinal)
                    ? new DestinationResolved(new LoginDestination.AuthorizationRequest(request))
                    : new DestinationRefused(IdentityRefusals.InvalidLink);

            case SignInLinkTarget.DirectLogin:
                // A direct login completes for the client the link was issued to.
                if (token.ClientApplicationId == null)
                {
                    return new DestinationRefused(IdentityRefusals.InvalidLink);
                }

                var client = await context.Set<SqlOSClientApplication>()
                    .FirstAsync(x => x.Id == token.ClientApplicationId, cancellationToken);
                return new DestinationResolved(new LoginDestination.DirectLogin(client, payload.RequestedOrganizationId));

            default:
                throw new InvalidOperationException($"Unknown sign-in link target '{target.GetType().Name}'.");
        }
    }

    private Task<SqlOSAuthorizationRequest?> FindActiveAuthorizationRequestAsync(string authorizationRequestId, DateTime now, CancellationToken cancellationToken)
        => ActiveAuthorizationRequests.FindAsync(context, authorizationRequestId, now, cancellationToken);

    private abstract record DestinationResolution;

    private sealed record DestinationResolved(LoginDestination Destination) : DestinationResolution;

    private sealed record DestinationRefused(IdentityRefusal Refusal) : DestinationResolution;
}

/// <summary>A sign-in link presented to the surface that received it.</summary>
internal sealed record CompleteMagicLinkSignInCommand(string? Token, SignInLinkTarget Target);

/// <summary>Where a sign-in link completes: each surface completes links as 7.2.1 did.</summary>
internal abstract record SignInLinkTarget
{
    private SignInLinkTarget()
    {
    }

    /// <summary>
    /// The hosted AuthPage: the authorization request the link was sent for, which must still be
    /// active and belong to the link's client, or the AuthPage's own sign-in when it was sent for none.
    /// </summary>
    public sealed record HostedPage : SignInLinkTarget
    {
        public static HostedPage Instance { get; } = new();
    }

    /// <summary>
    /// The headless API: the authorization request the link was sent for, which must be
    /// <see cref="RequestId"/> when the UI names one. The invitation the UI carries is bound to it
    /// before the sign-in completes.
    /// </summary>
    public sealed record Headless(string? RequestId, string? InvitationToken) : SignInLinkTarget;

    /// <summary>The public API: a direct login for the client the link was issued to; a link sent for an authorization request is refused.</summary>
    public sealed record DirectLogin : SignInLinkTarget
    {
        public static DirectLogin Instance { get; } = new();
    }
}

/// <summary>What completing a sign-in link did.</summary>
internal abstract record SignInLinkOutcome
{
    private SignInLinkOutcome()
    {
    }

    /// <summary>
    /// The link proved the mailbox, and the hub completed the login at <see cref="Destination"/>.
    /// <see cref="Email"/> is the address the link was delivered to.
    /// </summary>
    public sealed record SignedIn(LoginEvidence Evidence, LoginCompletion Completion, LoginDestination Destination, string Email) : SignInLinkOutcome;

    public sealed record Refused(IdentityRefusal Refusal) : SignInLinkOutcome;
}
