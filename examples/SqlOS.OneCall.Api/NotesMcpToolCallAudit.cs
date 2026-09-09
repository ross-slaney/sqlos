using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Extensions;

namespace SqlOS.OneCall.Api;

/// <summary>
/// Sample host filter: one SqlOS audit event per MCP tool call. Tool arguments, results, and
/// tokens are never written. This is application code, not a SqlOS surface.
/// </summary>
internal static class NotesMcpToolCallAudit
{
    public const string Action = "mcp.tool.called";
    public const string Source = "mcp";
    public const string TargetType = "mcp_tool";

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => async (context, cancellationToken) =>
        {
            var toolName = context.Params?.Name ?? string.Empty;
            try
            {
                var result = await next(context, cancellationToken).ConfigureAwait(false);
                await RecordAsync(
                    context,
                    toolName,
                    result.IsError == true ? "tool_error" : "succeeded",
                    failureKind: null,
                    cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await RecordAsync(context, toolName, "exception", ex.GetType().Name, cancellationToken)
                    .ConfigureAwait(false);
                throw;
            }
        };

    private static async Task RecordAsync(
        RequestContext<CallToolRequestParams> context,
        string toolName,
        string outcome,
        string? failureKind,
        CancellationToken cancellationToken)
    {
        var services = context.Services ?? context.Server.Services;
        if (services == null)
        {
            return;
        }

        var audit = services.GetService<ISqlOSAuditLogService>();
        if (audit == null)
        {
            return;
        }

        var httpContext = services.GetService<IHttpContextAccessor>()?.HttpContext;
        var token = httpContext?.GetSqlOSValidatedToken();
        var actor = token?.UserId != null
            ? new SqlOSAuditActor("user", token.UserId)
            : token?.ClientId != null
                ? new SqlOSAuditActor("client", token.ClientId)
                : new SqlOSAuditActor("anonymous");

        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tool"] = toolName,
            ["outcome"] = outcome,
            ["clientId"] = token?.ClientId,
            ["audience"] = token?.Audience,
            ["sessionId"] = token?.SessionId,
            ["mcpSessionId"] = context.Server.SessionId
        };
        if (failureKind != null)
        {
            metadata["failureKind"] = failureKind;
        }

        try
        {
            await audit.RecordAsync(
                new SqlOSAuditLogRecordRequest(
                    Action: Action,
                    OrganizationId: token?.OrganizationId,
                    UserId: token?.UserId,
                    ApplicationKey: token?.ClientId,
                    Source: Source,
                    Actor: actor,
                    Targets: [new SqlOSAuditTarget(TargetType, toolName)],
                    Context: httpContext != null ? SqlOSAuditContext.FromHttpContext(httpContext) : null,
                    Metadata: metadata),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            services.GetService<ILoggerFactory>()?
                .CreateLogger("SqlOS.OneCall.Api")
                .LogError(ex, "Failed to record the audit event for MCP tool {Tool}.", toolName);
        }
    }
}
