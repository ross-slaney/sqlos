using Microsoft.AspNetCore.Http;

namespace SqlOS.AuditLogs;

public sealed record SqlOSAuditActor(
    string Type,
    string? Id = null,
    string? DisplayName = null);

public sealed record SqlOSAuditTarget(
    string Type,
    string Id,
    string? DisplayName = null);

public sealed record SqlOSAuditContext(
    string? IpAddress = null,
    string? UserAgent = null,
    string? SessionId = null,
    string? RequestId = null,
    string? CorrelationId = null)
{
    public static SqlOSAuditContext FromHttpContext(HttpContext context)
    {
        var requestId = FirstHeader(context, "X-Request-ID")
            ?? FirstHeader(context, "X-Request-Id")
            ?? context.TraceIdentifier;
        var correlationId = FirstHeader(context, "X-Correlation-ID")
            ?? FirstHeader(context, "X-Correlation-Id");

        return new SqlOSAuditContext(
            IpAddress: context.Connection.RemoteIpAddress?.ToString(),
            UserAgent: context.Request.Headers.UserAgent.ToString(),
            SessionId: context.User.FindFirst("sid")?.Value,
            RequestId: requestId,
            CorrelationId: correlationId);
    }

    private static string? FirstHeader(HttpContext context, string name)
    {
        var value = context.Request.Headers[name].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

public sealed record SqlOSAuditLogRecordRequest(
    string Action,
    string? OrganizationId = null,
    string? UserId = null,
    string? ApplicationId = null,
    string? ApplicationKey = null,
    string Source = "application",
    SqlOSAuditActor? Actor = null,
    IReadOnlyList<SqlOSAuditTarget>? Targets = null,
    SqlOSAuditContext? Context = null,
    IReadOnlyDictionary<string, object?>? Metadata = null,
    string? IdempotencyKey = null,
    DateTime? OccurredAt = null);

public sealed record SqlOSAuditLogRecordResult(
    string EventId,
    bool Created,
    SqlOSAuditLogEvent Event);

public sealed record SqlOSAuditLogListRequest(
    int? Page = null,
    int? PageSize = null,
    string? OrganizationId = null,
    string? ApplicationId = null,
    string? ApplicationKey = null,
    string? Application = null,
    string? Source = null,
    string? Action = null,
    string? ActorType = null,
    string? ActorId = null,
    string? TargetType = null,
    string? TargetId = null,
    string? Result = null,
    string? Search = null,
    DateTime? OccurredAtFrom = null,
    DateTime? OccurredAtTo = null);

public sealed record SqlOSAuditLogListResult(
    IReadOnlyList<SqlOSAuditLogEvent> Data,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public sealed record SqlOSAuditLogEvent(
    string Id,
    string? OrganizationId,
    string? ApplicationId,
    string? ApplicationKey,
    string Source,
    string Action,
    string EventType,
    SqlOSAuditActor Actor,
    IReadOnlyList<SqlOSAuditTarget> Targets,
    SqlOSAuditContext? Context,
    IReadOnlyDictionary<string, object?>? Metadata,
    string? UserId,
    string? SessionId,
    string? IpAddress,
    string? UserAgent,
    string? RequestId,
    string? CorrelationId,
    DateTime OccurredAt,
    DateTime IngestedAt);

public sealed record SqlOSAuditLogCsvExportResult(
    string FileName,
    string Content);
