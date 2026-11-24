namespace SqlOS.AuthServer.Interfaces;

public interface ISqlOSAuthEmailSender
{
    bool IsConfigured { get; }

    Task SendAsync(SqlOSAuthEmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>A rendered message handed to an <see cref="ISqlOSAuthEmailSender"/>.</summary>
/// <param name="To">Recipient address.</param>
/// <param name="Subject">Rendered subject.</param>
/// <param name="HtmlBody">Rendered HTML body.</param>
/// <param name="TextBody">Rendered plain-text body, when available.</param>
/// <param name="Headers">
/// Extra message headers. SqlOS sets these when it falls back to this sender for transactional email
/// that carries headers such as <c>List-Unsubscribe</c>. Senders must copy every entry onto the outgoing message.
/// </param>
public sealed record SqlOSAuthEmailMessage(
    string To,
    string Subject,
    string HtmlBody,
    string? TextBody = null,
    IReadOnlyDictionary<string, string>? Headers = null);
