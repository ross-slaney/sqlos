using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

internal static class SqlOSSignupOrchestration
{
    public const int DisplayNameMaxLength = 200;
    public const int EmailMaxLength = 320;
    public const int OrganizationNameMaxLength = 200;

    public const string PasswordRequiredMessage = "Password is required.";
    public const string DisplayNameTooLongMessage = "Display name cannot exceed 200 characters.";
    public const string EmailTooLongMessage = "Email address cannot exceed 320 characters.";
    public const string OrganizationNameTooLongMessage = "Organization name cannot exceed 200 characters.";
    public const string InvitationEmailMismatchMessage = "This invitation was sent to another email address.";
    public const string UnauthorizedResourceMessage = "Requested resource is not allowed for this client.";

    public sealed record PasswordSignupInput(
        string DisplayName,
        string Email,
        string Password,
        string? OrganizationName);

    public static PasswordSignupInput NormalizePasswordSignup(
        string? displayName,
        string? email,
        string? password,
        string? organizationName,
        bool requirePassword = true)
    {
        var trimmedDisplayName = RequireBoundedText(
            displayName,
            "Display name is required.",
            DisplayNameMaxLength,
            DisplayNameTooLongMessage);
        var trimmedEmail = RequireBoundedText(
            email,
            "Email address is required.",
            EmailMaxLength,
            EmailTooLongMessage);
        if (requirePassword && string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(PasswordRequiredMessage);
        }

        string? trimmedOrganizationName = null;
        if (!string.IsNullOrWhiteSpace(organizationName))
        {
            trimmedOrganizationName = organizationName.Trim();
            if (trimmedOrganizationName.Length > OrganizationNameMaxLength)
            {
                throw new InvalidOperationException(OrganizationNameTooLongMessage);
            }
        }

        return new PasswordSignupInput(
            trimmedDisplayName,
            trimmedEmail,
            password ?? string.Empty,
            trimmedOrganizationName);
    }

    public static void RejectInvitationEmailMismatch(string? invitationEmail, string? requestedEmail)
    {
        if (string.IsNullOrWhiteSpace(invitationEmail) || string.IsNullOrWhiteSpace(requestedEmail))
        {
            return;
        }

        if (!string.Equals(
                SqlOSAdminService.NormalizeEmail(invitationEmail),
                SqlOSAdminService.NormalizeEmail(requestedEmail),
                StringComparison.Ordinal))
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

    public static async Task<SqlOSPasswordAuthenticationResult> CreatePasswordAccountAsync(
        SqlOSAdminService adminService,
        ISqlOSAuthServerDbContext context,
        PasswordSignupInput input,
        CancellationToken cancellationToken)
    {
        var user = await adminService.CreateUserAsync(
            new SqlOSCreateUserRequest(input.DisplayName, input.Email, input.Password),
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(input.OrganizationName))
        {
            var organization = await adminService.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest(input.OrganizationName, null),
                cancellationToken);
            await adminService.CreateMembershipAsync(
                organization.Id,
                new SqlOSCreateMembershipRequest(user.Id, "owner"),
                cancellationToken);
        }

        var organizations = await adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);
        return new SqlOSPasswordAuthenticationResult(user, organizations, "password");
    }

    public static async Task<T> ExecuteAsync<T>(
        ISqlOSAuthServerDbContext context,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (!SupportsDatabaseTransactions(context))
        {
            return await operation(cancellationToken);
        }

        if (context.Database.CurrentTransaction != null)
        {
            return await operation(cancellationToken);
        }

        var strategy = context.Database.CreateExecutionStrategy();
        var attempt = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0 && context is DbContext retryContext)
            {
                retryContext.ChangeTracker.Clear();
            }

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var result = await operation(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        });
    }

    public static bool SupportsDatabaseTransactions(ISqlOSAuthServerDbContext context)
        => !string.Equals(context.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);

    public static bool IsUniqueConstraintViolation(DbUpdateException exception)
        => exception.GetBaseException() is SqlException { Number: 2601 or 2627 };

    private static string RequireBoundedText(string? value, string requiredMessage, int maxLength, string tooLongMessage)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException(requiredMessage);
        }

        if (trimmed.Length > maxLength)
        {
            throw new InvalidOperationException(tooLongMessage);
        }

        return trimmed;
    }
}
