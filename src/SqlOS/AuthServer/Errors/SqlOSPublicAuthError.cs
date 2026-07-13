using System.Text.Json.Serialization;

namespace SqlOS.AuthServer.Errors;

public sealed record SqlOSPublicAuthError(
    string Error,
    string PublicMessage,
    int StatusCode,
    [property: JsonIgnore]
    string AuditReason,
    [property: JsonIgnore]
    string? DiagnosticMessage,
    [property: JsonIgnore]
    bool IsDiagnosticOnly)
{
    [JsonIgnore]
    public bool HasDiagnosticDetail
        => IsDiagnosticOnly
            && !string.IsNullOrWhiteSpace(DiagnosticMessage)
            && !string.Equals(DiagnosticMessage, PublicMessage, StringComparison.Ordinal);
}
