namespace SqlOS.Email.Models;

public sealed class SqlOSEmailTemplate
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string SubjectTemplate { get; set; } = string.Empty;
    public string HtmlBodyTemplate { get; set; } = string.Empty;
    public string TextBodyTemplate { get; set; } = string.Empty;
    public string VariablesJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class SqlOSEmailDelivery
{
    public string Id { get; set; } = string.Empty;
    public string? TemplateId { get; set; }
    public string TemplateKey { get; set; } = string.Empty;
    public int TemplateVersion { get; set; }
    public string To { get; set; } = string.Empty;
    public string Status { get; set; } = SqlOSEmailDeliveryStatuses.Pending;
    public string? ProviderMessageId { get; set; }
    public string? SanitizedError { get; set; }
    public string RenderedSubject { get; set; } = string.Empty;
    public string RenderedTextPreview { get; set; } = string.Empty;
    public string? RenderedHtmlPreview { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? FailedAt { get; set; }

    public SqlOSEmailTemplate? Template { get; set; }
}

public static class SqlOSEmailDeliveryStatuses
{
    public const string Pending = "pending";
    public const string Queued = "queued";
    public const string Failed = "failed";
}
