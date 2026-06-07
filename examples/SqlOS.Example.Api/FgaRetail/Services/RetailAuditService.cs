using Microsoft.EntityFrameworkCore;
using SqlOS.AuditLogs;
using SqlOS.Example.Api.Data;
using SqlOS.Example.Api.FgaRetail.Middleware;
using SqlOS.Fga.Models;

namespace SqlOS.Example.Api.FgaRetail.Services;

public sealed class RetailAuditService
{
    public const string ApplicationKey = "northwind-retail";

    private readonly ISqlOSAuditLogService _auditLogs;

    public RetailAuditService(ISqlOSAuditLogService auditLogs)
    {
        _auditLogs = auditLogs;
    }

    public async Task RecordAsync(
        HttpContext http,
        ExampleAppDbContext context,
        string action,
        IReadOnlyList<SqlOSAuditTarget> targets,
        IReadOnlyDictionary<string, object?>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var subjectId = http.GetSubjectId();
        var profile = await context.ExampleUserProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.SqlOSUserId == subjectId, cancellationToken);
        var subject = profile == null
            ? await context.Set<SqlOSFgaSubject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == subjectId, cancellationToken)
            : null;
        var auditContext = SqlOSAuditContext.FromHttpContext(http);
        var actorType = profile != null ? "user" : subject?.SubjectTypeId ?? "subject";
        var actorName = profile?.DisplayName ?? subject?.DisplayName;
        var requestKey = auditContext.RequestId ?? http.TraceIdentifier;

        await _auditLogs.RecordAsync(
            new SqlOSAuditLogRecordRequest(
                Action: action,
                OrganizationId: profile?.OrganizationId ?? subject?.OrganizationId,
                UserId: profile?.SqlOSUserId,
                ApplicationKey: ApplicationKey,
                Source: "application",
                Actor: new SqlOSAuditActor(actorType, subjectId, actorName),
                Targets: targets,
                Context: auditContext,
                Metadata: metadata,
                IdempotencyKey: $"{ApplicationKey}:{action}:{string.Join(":", targets.Select(x => x.Id))}:{requestKey}"),
            cancellationToken);
    }
}
