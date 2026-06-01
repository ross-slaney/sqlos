using System.Text.Json.Nodes;

namespace SqlOS.Email.Contracts;

public sealed record SqlOSSendEmailRequest(
    string TemplateKey,
    string To,
    IReadOnlyDictionary<string, object?> Variables,
    string? IdempotencyKey = null);

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

public sealed record SqlOSEmailMessage(
    string To,
    string Subject,
    string HtmlBody,
    string TextBody);
