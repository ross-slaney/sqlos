using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Services;

namespace SqlOS.Tests;

internal static class TestCryptoService
{
    public static SqlOSCryptoService Create(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        IDataProtectionProvider? dataProtectionProvider = null)
    {
        return new SqlOSCryptoService(
            context,
            options,
            dataProtectionProvider ?? new EphemeralDataProtectionProvider());
    }
}
