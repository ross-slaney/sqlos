namespace SqlOS.AuditLogs;

public interface ISqlOSAuditLogService
{
    Task<SqlOSAuditLogRecordResult> RecordAsync(
        SqlOSAuditLogRecordRequest request,
        CancellationToken cancellationToken = default);

    Task<SqlOSAuditLogListResult> ListAsync(
        SqlOSAuditLogListRequest request,
        CancellationToken cancellationToken = default);

    Task<SqlOSAuditLogEvent?> GetAsync(
        string id,
        CancellationToken cancellationToken = default);

    Task<SqlOSAuditLogCsvExportResult> ExportCsvAsync(
        SqlOSAuditLogListRequest request,
        CancellationToken cancellationToken = default);
}
