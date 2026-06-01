using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSMfaPolicyService
{
    private readonly SqlOSAuthServerOptions _options;

    public SqlOSMfaPolicyService(IOptions<SqlOSAuthServerOptions> options)
    {
        _options = options.Value;
    }

    public bool SatisfiesStrongMfa(string? authenticationMethod)
    {
        if (string.IsNullOrWhiteSpace(authenticationMethod))
        {
            return false;
        }

        if (string.Equals(authenticationMethod, "phone_otp", StringComparison.OrdinalIgnoreCase))
        {
            return _options.PhoneOtp.SatisfiesMfa;
        }

        return string.Equals(authenticationMethod, "totp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(authenticationMethod, "passkey", StringComparison.OrdinalIgnoreCase)
            || string.Equals(authenticationMethod, "webauthn", StringComparison.OrdinalIgnoreCase);
    }

    public bool SatisfiesAdminOwnerStrongMfa(string? authenticationMethod)
        => !string.Equals(authenticationMethod, "phone_otp", StringComparison.OrdinalIgnoreCase)
            && SatisfiesStrongMfa(authenticationMethod);
}
