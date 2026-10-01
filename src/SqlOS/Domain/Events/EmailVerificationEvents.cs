using SqlOS.AuthServer.Models;

namespace SqlOS.Domain.Events;

/// <summary>An email-verification link was created for an account's address (<c>user.email-verification-token-created</c>).</summary>
internal sealed record EmailVerificationTokenCreated(string TokenId, string UserId) : TemporaryTokenOutcome(TokenId)
{
    public override TemporaryTokenKind Kind => SqlOSTemporaryTokenKinds.EmailVerification;
}
