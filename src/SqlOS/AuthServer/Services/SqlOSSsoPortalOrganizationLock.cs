using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Database;

namespace SqlOS.AuthServer.Services;

internal static class SqlOSSsoPortalOrganizationLock
{
    internal static string GetResource(string organizationId)
        => $"SqlOS:SsoPortalOrganization:{organizationId}";

    internal static async Task AcquireAsync(
        ISqlOSAuthServerDbContext context,
        string organizationId,
        CancellationToken cancellationToken)
    {
        if (context.Database.IsRelational() && context.Database.CurrentTransaction == null)
        {
            throw new InvalidOperationException("The SSO portal organization lock requires an active transaction.");
        }

        await SqlOSDatabase.AcquireExclusiveTransactionLockAsync(
            context.Database,
            GetResource(organizationId),
            TimeSpan.FromSeconds(30),
            "Could not acquire the SSO portal organization lock.",
            cancellationToken);
    }
}
