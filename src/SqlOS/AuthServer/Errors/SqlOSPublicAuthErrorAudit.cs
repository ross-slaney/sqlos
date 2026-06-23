using Microsoft.AspNetCore.Http;
using SqlOS.AuthServer.Services;

namespace SqlOS.AuthServer.Errors;

public static class SqlOSPublicAuthErrorAudit
{
    public const string EventType = "auth.public_error.mapped";

    public static async Task RecordIfDiagnosticAsync(
        SqlOSAdminService adminService,
        HttpContext httpContext,
        SqlOSPublicAuthErrorSurface surface,
        Exception exception,
        SqlOSPublicAuthError error,
        CancellationToken cancellationToken = default)
    {
        if (!error.HasDiagnosticDetail)
        {
            return;
        }

        try
        {
            await adminService.RecordAuditAsync(
                EventType,
                "system",
                "authserver",
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                data: new
                {
                    surface = surface.ToString(),
                    error = error.Error,
                    publicMessage = error.PublicMessage,
                    auditReason = error.AuditReason,
                    failureType = exception.GetType().FullName,
                    diagnosticMessage = error.DiagnosticMessage
                },
                cancellationToken: cancellationToken);
        }
        catch
        {
            // Do not let an audit write failure change the protocol response.
        }
    }
}
