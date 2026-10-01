using SqlOS.AuthServer.Models;
using SqlOS.Domain;

namespace SqlOS.AuditLogs.Projections;

/// <summary>
/// The rows 7.2.1's <c>SqlOSAdminService.RecordAuditAsync</c> wrote, built for a projected event:
/// source <c>authserver</c>, the given actor, and the IP address and metadata the 7.2.1 call site
/// passed. A projection passes its metadata as the same anonymous shape the call site built, so
/// member names, their order and their values serialize to the same JSON.
/// </summary>
internal static class AuthServerAuditRows
{
    /// <summary>A row recorded by SqlOS itself (actor <c>system</c>, no actor ID).</summary>
    public static SqlOSAuditEvent System(
        string eventType,
        SqlOSAuditProjectionContext context,
        string? userId = null,
        string? ipAddress = null,
        object? data = null)
        => SqlOSAuditRows.Create(
            SqlOSAuditRows.AuthServerRequest(eventType, "system", null, userId: userId, ipAddress: ipAddress, data: data),
            SqlOSIds.New("evt"),
            context.Now);

    /// <summary>A row whose actor is the user it is about (actor <c>user</c>, the user's ID).</summary>
    public static SqlOSAuditEvent User(
        string eventType,
        string userId,
        SqlOSAuditProjectionContext context,
        string? ipAddress = null,
        object? data = null,
        string? organizationId = null)
        => SqlOSAuditRows.Create(
            SqlOSAuditRows.AuthServerRequest(eventType, "user", userId, userId: userId, organizationId: organizationId, ipAddress: ipAddress, data: data),
            SqlOSIds.New("evt"),
            context.Now);
}
