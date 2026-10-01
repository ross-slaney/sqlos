using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Deactivates an account at the host's request (<see cref="SqlOSAdminService.DeactivateUserAsync"/>).
/// SqlOS then refuses the account's sign-ins, and its sessions and tokens at their next use, as it
/// does for every inactive account; nothing else changes, as when 7.x hosts set the flag
/// themselves.
/// </summary>
internal sealed class DeactivateUser(ISqlOSAuthServerDbContext context, TimeProvider time)
{
    /// <summary>The reason the account's deactivation records.</summary>
    public const string Reason = "deactivated";

    /// <returns>The account, or <see langword="null"/> when there is none.</returns>
    public async Task<SqlOSUser?> ExecuteAsync(DeactivateUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = await context.FindUserAsync(command.UserId, SqlOSUserParts.None, cancellationToken);
        if (user == null)
        {
            return null;
        }

        user.Deactivate(Reason, time.GetUtcNow().UtcDateTime);
        await context.SaveChangesAsync(cancellationToken);
        return user;
    }
}

/// <summary>Reactivates an account at the host's request (<see cref="SqlOSAdminService.ReactivateUserAsync"/>).</summary>
internal sealed class ReactivateUser(ISqlOSAuthServerDbContext context, TimeProvider time)
{
    /// <returns>The account, or <see langword="null"/> when there is none.</returns>
    public async Task<SqlOSUser?> ExecuteAsync(ReactivateUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = await context.FindUserAsync(command.UserId, SqlOSUserParts.None, cancellationToken);
        if (user == null)
        {
            return null;
        }

        user.Reactivate(time.GetUtcNow().UtcDateTime);
        await context.SaveChangesAsync(cancellationToken);
        return user;
    }
}

internal sealed record DeactivateUserCommand(string UserId);

internal sealed record ReactivateUserCommand(string UserId);
