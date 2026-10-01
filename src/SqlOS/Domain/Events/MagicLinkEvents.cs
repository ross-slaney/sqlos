using SqlOS.AuthServer.Models;

namespace SqlOS.Domain.Events;

// Sign-in links: temporary tokens of the magic-link kind, and the link flow's failure records. The
// audited ones carry exactly what 7.2.1's magic_link.* rows recorded; a link's own outcomes come
// from its payload, which holds the masked address, the address the request came from and the
// sign-in context.

/// <summary>A sign-in link went out to its stored recipient, or was withheld (<c>magic_link.requested</c>).</summary>
internal sealed record MagicLinkRequested(
    string TokenId,
    string MaskedEmail,
    string? IpAddress,
    string? ClientApplicationId,
    string? AuthorizationRequestId,
    string? RequestedOrganizationId,
    bool Sent) : TemporaryTokenOutcome(TokenId)
{
    public override TemporaryTokenKind Kind => SqlOSTemporaryTokenKinds.MagicLink;
}

/// <summary>The link could not be sent, so it was withdrawn (<c>magic_link.send_failed</c>).</summary>
internal sealed record MagicLinkDeliveryFailed(
    string TokenId,
    string MaskedEmail,
    string? IpAddress,
    string? ClientApplicationId,
    string? AuthorizationRequestId,
    string? RequestedOrganizationId) : TemporaryTokenOutcome(TokenId)
{
    public override TemporaryTokenKind Kind => SqlOSTemporaryTokenKinds.MagicLink;
}

/// <summary>A sign-in link was opened and signed its account in (<c>magic_link.completed</c>).</summary>
internal sealed record MagicLinkCompleted(
    string TokenId,
    string MaskedEmail,
    string? IpAddress,
    string UserId,
    string? ClientApplicationId,
    string? AuthorizationRequestId,
    string? RequestedOrganizationId) : TemporaryTokenOutcome(TokenId)
{
    public override TemporaryTokenKind Kind => SqlOSTemporaryTokenKinds.MagicLink;
}

/// <summary>
/// A failure record: a link was refused because a send limit is reached; <paramref name="Limit"/>
/// names it (<c>magic_link.rate_limit_rejected</c>).
/// </summary>
internal sealed record MagicLinkSendRateLimited(
    string MaskedEmail,
    string? IpAddress,
    string Limit,
    string? ClientApplicationId,
    string? RequestedOrganizationId) : ISqlOSDomainEvent;

/// <summary>A failure record: a presented link is unknown, expired or spent (<c>magic_link.rejected</c>).</summary>
internal sealed record MagicLinkNotFound : ISqlOSDomainEvent;

/// <summary>A failure record: a concurrent request spent the presented link first (<c>magic_link.rejected</c>).</summary>
internal sealed record MagicLinkReplayed(
    string MaskedEmail,
    string? IpAddress,
    string? ClientApplicationId,
    string? AuthorizationRequestId) : ISqlOSDomainEvent;
