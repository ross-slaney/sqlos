using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// An operator or host code creates an account (<see cref="Services.SqlOSAdminService.CreateUserAsync"/>:
/// the admin API, the dashboard, seeds and host code).
/// </summary>
/// <remarks>
/// Registration is the step every sign-up shares (<see cref="AccountRegistration"/>): the address is
/// unverified, a later proof of its mailbox claims it (#420), and a password, when one is given, is
/// set under the password policy; a request without one creates an account without a password, as
/// an operator may. An address an account already owns, or one that is not an email address, fails
/// as in 7.x, with the exception the admin API answers as a server error. Creating a user writes no
/// audit row in 7.2.1 (#415).
/// </remarks>
internal sealed class CreateUser(ISqlOSAuthServerDbContext context, TimeProvider clock)
{
    public Task<SqlOSUser> ExecuteAsync(CreateUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return AccountRegistration.RegisterAsync(
            context,
            command.DisplayName,
            command.Email,
            command.Password,
            proof: null,
            clock.GetUtcNow().UtcDateTime,
            cancellationToken);
    }
}

/// <summary>An account to create.</summary>
internal sealed record CreateUserCommand(string DisplayName, string Email, string? Password);
