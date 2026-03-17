namespace SqlOS.Example.Api.Models;

public sealed class ExampleUserProfile
{
    public string Id { get; set; } = $"prof_{Guid.NewGuid():N}"[..29];
    public string SqlOSUserId { get; set; } = string.Empty;
    public string DefaultEmail { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public string? OrganizationName { get; set; }
    public string ReferralSource { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
