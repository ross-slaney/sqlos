using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Processes.Identity;
using SqlOS.Domain;

namespace SqlOS.IntegrationTests.Infrastructure;

/// <summary>
/// Registers accounts for tests the way SqlOS registers them (<see cref="AccountRegistration"/>).
/// An account whose address a sign-up's code proved is registered verified; an operator-created
/// account (<c>SqlOSAdminService.CreateUserAsync</c>) never is.
/// </summary>
internal static class TestAccounts
{
    /// <summary>
    /// Registers the account a sign-up would: its address verified when <paramref name="emailProof"/>
    /// proves it, and the request's password, if any, under the password policy.
    /// </summary>
    public static Task<SqlOSUser> RegisterAsync(
        ISqlOSAuthServerDbContext context,
        SqlOSCreateUserRequest request,
        OwnershipProof? emailProof,
        CancellationToken cancellationToken = default)
        => AccountRegistration.RegisterAsync(
            context,
            request.DisplayName,
            request.Email,
            request.Password,
            emailProof,
            DateTime.UtcNow,
            cancellationToken);
}
