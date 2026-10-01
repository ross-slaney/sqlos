using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Policies;
using SqlOS.Database;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// What the surfaces still check around a sign-up before its process runs (an invitation sent to
/// another address, the authorization request's client and resource), the input bounds the sign-up
/// processes apply, and the provider facts they ask (transactions, unique violations).
/// </summary>
internal static class SqlOSSignupOrchestration
{
    public const int DisplayNameMaxLength = 200;
    public const int EmailMaxLength = 320;
    public const int OrganizationNameMaxLength = 200;

    public const string PasswordRequiredMessage = PasswordIsNotBlank.Message;
    public const string DisplayNameTooLongMessage = "Display name cannot exceed 200 characters.";
    public const string EmailTooLongMessage = "Email address cannot exceed 320 characters.";
    public const string OrganizationNameTooLongMessage = "Organization name cannot exceed 200 characters.";
    public const string InvitationEmailMismatchMessage = "This invitation was sent to another email address.";
    public const string UnauthorizedResourceMessage = "Requested resource is not allowed for this client.";

    public static void RejectInvitationEmailMismatch(string? invitationEmail, string? requestedEmail)
    {
        if (string.IsNullOrWhiteSpace(invitationEmail) || string.IsNullOrWhiteSpace(requestedEmail))
        {
            return;
        }

        if (!SqlOSEmailAddress.IsSameMailbox(invitationEmail, requestedEmail))
        {
            throw new InvalidOperationException(InvitationEmailMismatchMessage);
        }
    }

    public static void RejectUnauthorizedResource(
        SqlOSAuthServerOptions options,
        string? resource)
    {
        if (options.ResourceIndicators.Enabled || string.IsNullOrWhiteSpace(resource))
        {
            return;
        }

        throw new InvalidOperationException(UnauthorizedResourceMessage);
    }

    public static async Task EnsureAuthorizationSignupContextAsync(
        SqlOSAdminService adminService,
        ISqlOSAuthServerDbContext context,
        SqlOSAuthServerOptions options,
        SqlOSAuthorizationRequest authorizationRequest,
        CancellationToken cancellationToken)
    {
        var client = authorizationRequest.ClientApplication
            ?? await context.Set<SqlOSClientApplication>()
                .FirstOrDefaultAsync(x => x.Id == authorizationRequest.ClientApplicationId, cancellationToken)
            ?? throw new InvalidOperationException("Client application is required.");

        await adminService.RequireClientAsync(client.ClientId, authorizationRequest.RedirectUri, cancellationToken);
        RejectUnauthorizedResource(options, authorizationRequest.Resource);
    }

    public static bool SupportsDatabaseTransactions(ISqlOSAuthServerDbContext context)
        => !string.Equals(context.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);

    public static bool IsUniqueConstraintViolation(DbUpdateException exception)
        => SqlOSDatabaseErrors.IsUniqueConstraintViolation(exception);
}
