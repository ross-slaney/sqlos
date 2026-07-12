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
        // Unit tests use an in-process key ring. Production startup deliberately requires
        // an explicit persisted-and-shared key-ring attestation or another custody provider.
        options.Value.SigningKeyCustody.DataProtectionKeyRingIsPersistedAndShared = true;

        return new SqlOSCryptoService(
            context,
            options,
            dataProtectionProvider ?? new EphemeralDataProtectionProvider());
    }
}
