namespace SqlOS.AuthServer.Errors;

public sealed record SqlOSPublicAuthError(
    string Error,
    string PublicMessage,
    int StatusCode,
    string AuditReason,
    string? DiagnosticMessage,
    bool IsDiagnosticOnly)
{
    public bool HasDiagnosticDetail
        => IsDiagnosticOnly
            && !string.IsNullOrWhiteSpace(DiagnosticMessage)
            && !string.Equals(DiagnosticMessage, PublicMessage, StringComparison.Ordinal);
}
