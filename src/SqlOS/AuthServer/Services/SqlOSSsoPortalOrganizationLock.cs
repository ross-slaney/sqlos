using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;

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
        if (!string.Equals(
                context.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.SqlServer",
                StringComparison.Ordinal))
        {
            return;
        }

        if (context.Database.CurrentTransaction == null)
        {
            throw new InvalidOperationException("The SSO portal organization lock requires an active transaction.");
        }

        var resource = GetResource(organizationId);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 30000;
            IF @result < 0 THROW 51000, 'Could not acquire the SSO portal organization lock.', 1;
            """, cancellationToken);
    }
}
