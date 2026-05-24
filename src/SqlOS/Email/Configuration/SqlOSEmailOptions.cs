namespace SqlOS.Email.Configuration;

public sealed class SqlOSEmailOptions
{
    public string? AzureCommunicationServicesConnectionString { get; set; }
    public string? FromAddress { get; set; }
    public TimeSpan DeliveryRetention { get; set; } = TimeSpan.FromDays(90);
    public bool EnableIdempotency { get; set; } = true;
    public bool PersistRenderedHtmlPreview { get; set; }
    public int RenderedTextPreviewMaxLength { get; set; } = 4000;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AzureCommunicationServicesConnectionString)
        && !string.IsNullOrWhiteSpace(FromAddress);
}
