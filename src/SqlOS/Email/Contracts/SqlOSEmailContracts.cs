using System.Text.Json.Nodes;

namespace SqlOS.Email.Contracts;

public sealed record SqlOSSendEmailRequest(
    string TemplateKey,
    string To,
    IReadOnlyDictionary<string, object?> Variables,
    string? IdempotencyKey = null,
    SqlOSEmailListUnsubscribe? ListUnsubscribe = null);

/// <summary>
/// Unsubscribe targets that SqlOS writes to the RFC 2369 <c>List-Unsubscribe</c> header.
/// Supply at least one target.
/// </summary>
/// <param name="OneClickUrl">
/// Absolute HTTPS URL that unsubscribes the recipient when it receives an RFC 8058
/// <c>POST</c> with the body <c>List-Unsubscribe=One-Click</c>, without further interaction.
/// SqlOS also sends <c>List-Unsubscribe-Post: List-Unsubscribe=One-Click</c> when this is set.
/// </param>
/// <param name="MailTo">Optional <c>mailto:</c> URI that unsubscribes the recipient by email.</param>
public sealed record SqlOSEmailListUnsubscribe(
    Uri? OneClickUrl = null,
    Uri? MailTo = null);

public sealed record SqlOSSendEmailResult(
    string DeliveryId,
    string Status,
    string TemplateKey,
    int TemplateVersion,
    string? ProviderMessageId,
    string? SanitizedError);

public sealed record SqlOSRenderedEmailPreview(
    string Subject,
    string HtmlBody,
    string TextBody,
    IReadOnlyList<string> Variables);

public sealed record SqlOSCreateEmailTemplateRequest(
    string Key,
    string DisplayName,
    string SubjectTemplate,
    string HtmlBodyTemplate,
    string TextBodyTemplate,
    JsonObject? Variables = null,
    bool IsActive = true);

public sealed record SqlOSUpdateEmailTemplateRequest(
    string Key,
    string DisplayName,
    string SubjectTemplate,
    string HtmlBodyTemplate,
    string TextBodyTemplate,
    JsonObject? Variables = null,
    bool IsActive = true);

public sealed record SqlOSPreviewEmailTemplateRequest(JsonObject? Variables = null);

public sealed record SqlOSEmailProviderResult(string? ProviderMessageId);

/// <summary>A rendered message handed to an <see cref="Interfaces.ISqlOSEmailSender"/>.</summary>
/// <param name="To">Recipient address.</param>
/// <param name="Subject">Rendered subject.</param>
/// <param name="HtmlBody">Rendered HTML body.</param>
/// <param name="TextBody">Rendered plain-text body.</param>
/// <param name="Headers">
/// Extra message headers, such as <c>List-Unsubscribe</c>. Senders must copy every entry onto the outgoing message.
/// </param>
public sealed record SqlOSEmailMessage(
    string To,
    string Subject,
    string HtmlBody,
    string TextBody,
    IReadOnlyDictionary<string, string>? Headers = null);
