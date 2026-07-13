using Microsoft.AspNetCore.Http;

namespace SqlOS.AuthServer.Errors;

public sealed class SqlOSPublicAuthException : InvalidOperationException
{
    public SqlOSPublicAuthException(
        string error,
        string publicMessage,
        int statusCode = StatusCodes.Status400BadRequest,
        string? auditReason = null,
        string? diagnosticMessage = null,
        Exception? innerException = null)
        : base(publicMessage, innerException)
    {
        Error = string.IsNullOrWhiteSpace(error) ? "invalid_request" : error;
        PublicMessage = string.IsNullOrWhiteSpace(publicMessage)
            ? SqlOSPublicAuthErrorMapper.DefaultRequestMessage
            : publicMessage;
        StatusCode = statusCode;
        AuditReason = string.IsNullOrWhiteSpace(auditReason) ? Error : auditReason;
        DiagnosticMessage = string.IsNullOrWhiteSpace(diagnosticMessage) ? null : diagnosticMessage;
    }

    public string Error { get; }

    public string PublicMessage { get; }

    public int StatusCode { get; }

    public string AuditReason { get; }

    public string? DiagnosticMessage { get; }
}
