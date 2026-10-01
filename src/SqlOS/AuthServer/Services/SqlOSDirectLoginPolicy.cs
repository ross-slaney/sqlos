using Microsoft.AspNetCore.Http;
using SqlOS.AuthServer.Errors;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;
using SqlOS.Hosting;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Direct login (the <see cref="SqlOSAuthService"/> password, signup, email/phone code, magic-link,
/// organization-selection and MFA completions, and the <c>/oidc/*</c> browser routes) returns tokens
/// to the caller with no user-agent redirect where a consent screen could appear. Only first-party
/// clients may use it; every other client signs users in through <c>/authorize</c>, whose consent gate
/// applies. This is the same <see cref="SqlOSClientApplication.IsFirstParty"/> rule that skips consent,
/// so it is a secure default rather than an operator switch.
/// </summary>
internal static class SqlOSDirectLoginPolicy
{
    public const string RejectedAuditEvent = "oauth.direct_login.rejected";
    public const string RejectedError = "invalid_client";
    public const string RejectedMessage = "This client must use the authorization endpoint to sign users in.";

    /// <summary>
    /// Returns when <paramref name="client"/> is first-party. Otherwise records one
    /// <see cref="RejectedAuditEvent"/> and throws the generic public <c>invalid_client</c> error.
    /// Callers run this before minting any token, challenge, email, provider state, or session.
    /// </summary>
    public static Task EnsureFirstPartyAsync(
        SqlOSAdminService adminService,
        SqlOSClientApplication client,
        HttpContext? httpContext,
        string? userId,
        CancellationToken cancellationToken)
        => EnsureFirstPartyAsync(
            adminService,
            client,
            SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.PublicApi),
            userId,
            cancellationToken);

    /// <summary>
    /// The same gate for a process, which knows the request only as a
    /// <see cref="SqlOSRequestContext"/>: its address and route are the ones the refusal records.
    /// </summary>
    public static async Task EnsureFirstPartyAsync(
        SqlOSAdminService adminService,
        SqlOSClientApplication client,
        SqlOSRequestContext request,
        string? userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (client.IsFirstParty)
        {
            return;
        }

        await adminService.RecordAuditAsync(
            RejectedAuditEvent,
            "client",
            client.Id,
            userId: userId,
            ipAddress: request.IpAddress,
            data: new
            {
                client_application_id = client.Id,
                client_id = client.ClientId,
                route = request.Route
            },
            cancellationToken: cancellationToken);

        throw new SqlOSPublicAuthException(
            RejectedError,
            RejectedMessage,
            StatusCodes.Status400BadRequest,
            auditReason: "direct_login_client_not_first_party");
    }
}
