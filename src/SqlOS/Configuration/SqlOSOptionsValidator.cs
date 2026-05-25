using SqlOS.AuthServer.Configuration;

namespace SqlOS.Configuration;

internal static class SqlOSOptionsValidator
{
    public static void ValidateOrThrow(SqlOSOptions options)
    {
        var errors = new List<string>();

        var dashboardBasePath = ValidateRootPath(options.DashboardBasePath, nameof(options.DashboardBasePath), errors);
        var authBasePath = ValidateRootPath(options.AuthServer.BasePath, "AuthServer.BasePath", errors);

        if (dashboardBasePath != null && authBasePath != null)
        {
            var expectedAuthBasePath = BuildAuthBasePath(dashboardBasePath);
            if (!string.Equals(authBasePath, expectedAuthBasePath, StringComparison.Ordinal))
            {
                errors.Add($"AuthServer.BasePath must be '{expectedAuthBasePath}' when DashboardBasePath is '{dashboardBasePath}'.");
            }
        }

        if (options.Dashboard.AuthMode == SqlOSDashboardAuthMode.Password
            && string.IsNullOrWhiteSpace(options.Dashboard.Password))
        {
            errors.Add("Dashboard.Password is required when Dashboard.AuthMode is Password.");
        }

        if (options.Dashboard.SessionLifetime <= TimeSpan.Zero)
        {
            errors.Add("Dashboard.SessionLifetime must be greater than zero.");
        }

        ValidateDashboardLoginThrottlingOptions(options.Dashboard.LoginThrottling, errors);

        var issuer = ValidateAbsoluteUri(options.AuthServer.Issuer, "AuthServer.Issuer", errors);
        if (issuer != null && authBasePath != null)
        {
            var issuerPath = NormalizeRootPath(issuer.AbsolutePath);
            if (!string.Equals(issuerPath, authBasePath, StringComparison.Ordinal))
            {
                errors.Add($"AuthServer.Issuer path must match AuthServer.BasePath. Expected path '{authBasePath}' but got '{issuerPath}'.");
            }
        }

        Uri? publicOrigin = null;
        if (!string.IsNullOrWhiteSpace(options.AuthServer.PublicOrigin))
        {
            publicOrigin = ValidateAbsoluteUri(options.AuthServer.PublicOrigin, "AuthServer.PublicOrigin", errors);
            if (publicOrigin != null)
            {
                if (!string.IsNullOrWhiteSpace(publicOrigin.Query) || !string.IsNullOrWhiteSpace(publicOrigin.Fragment))
                {
                    errors.Add("AuthServer.PublicOrigin cannot include a query string or fragment.");
                }

                if (!string.Equals(publicOrigin.AbsolutePath, "/", StringComparison.Ordinal))
                {
                    errors.Add("AuthServer.PublicOrigin must be an origin only, without a path.");
                }
            }
        }

        if (publicOrigin != null && authBasePath != null)
        {
            var expectedIssuer = $"{publicOrigin.GetLeftPart(UriPartial.Authority).TrimEnd('/')}{authBasePath}";
            if (!string.Equals(options.AuthServer.Issuer.TrimEnd('/'), expectedIssuer, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"AuthServer.Issuer must be '{expectedIssuer}' when AuthServer.PublicOrigin is set.");
            }
        }

        ValidateHeadlessOptions(options.AuthServer.Headless, errors);
        ValidateEmailOtpOptions(options.AuthServer.EmailOtp, errors);
        ValidatePasswordLoginAbuseOptions(options.AuthServer.PasswordLogin, errors);
        ValidateClientRegistrationOptions(options.AuthServer, errors);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid SqlOS configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(static error => $"- {error}")));
        }
    }

    private static void ValidateDashboardLoginThrottlingOptions(SqlOSDashboardLoginThrottlingOptions options, List<string> errors)
    {
        if (!options.Enabled)
        {
            return;
        }

        if (options.MaxFailuresPerIp <= 0)
        {
            errors.Add("Dashboard.LoginThrottling.MaxFailuresPerIp must be greater than zero.");
        }

        if (options.MaxGlobalFailures <= 0)
        {
            errors.Add("Dashboard.LoginThrottling.MaxGlobalFailures must be greater than zero.");
        }

        if (options.Window <= TimeSpan.Zero)
        {
            errors.Add("Dashboard.LoginThrottling.Window must be greater than zero.");
        }

        if (options.LockoutDuration <= TimeSpan.Zero)
        {
            errors.Add("Dashboard.LoginThrottling.LockoutDuration must be greater than zero.");
        }
    }

    private static void ValidateHeadlessOptions(SqlOSHeadlessAuthOptions options, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(options.HeadlessApiBasePath))
        {
            ValidateRootPath(options.HeadlessApiBasePath, "AuthServer.Headless.HeadlessApiBasePath", errors);
            if (!options.EnableApi)
            {
                errors.Add("AuthServer.Headless.HeadlessApiBasePath requires AuthServer.Headless.EnableApi.");
            }
        }

        if (options.OnHeadlessSignupAsync != null && !options.EnableApi)
        {
            errors.Add("AuthServer.Headless.OnHeadlessSignupAsync requires AuthServer.Headless.EnableApi.");
        }
    }

    private static void ValidatePasswordLoginAbuseOptions(SqlOSPasswordLoginAbuseOptions options, List<string> errors)
    {
        if (!options.Enabled)
        {
            return;
        }

        if (options.MaxFailedAttemptsPerAccount <= 0)
        {
            errors.Add("AuthServer.PasswordLogin.MaxFailedAttemptsPerAccount must be greater than zero.");
        }

        if (options.MaxFailedAttemptsPerIp <= 0)
        {
            errors.Add("AuthServer.PasswordLogin.MaxFailedAttemptsPerIp must be greater than zero.");
        }

        if (options.MaxFailedAttemptsPerClient <= 0)
        {
            errors.Add("AuthServer.PasswordLogin.MaxFailedAttemptsPerClient must be greater than zero.");
        }

        if (options.MaxFailedAttemptsPerDevice <= 0)
        {
            errors.Add("AuthServer.PasswordLogin.MaxFailedAttemptsPerDevice must be greater than zero.");
        }

        if (options.FailureWindow <= TimeSpan.Zero)
        {
            errors.Add("AuthServer.PasswordLogin.FailureWindow must be greater than zero.");
        }

        if (options.LockoutDuration <= TimeSpan.Zero)
        {
            errors.Add("AuthServer.PasswordLogin.LockoutDuration must be greater than zero.");
        }
    }

    private static void ValidateClientRegistrationOptions(SqlOSAuthServerOptions options, List<string> errors)
    {
        if (options.ClientRegistration.Cimd.DefaultCacheTtl <= TimeSpan.Zero)
        {
            errors.Add("AuthServer.ClientRegistration.Cimd.DefaultCacheTtl must be greater than zero.");
        }

        if (options.ClientRegistration.Cimd.HttpTimeout <= TimeSpan.Zero)
        {
            errors.Add("AuthServer.ClientRegistration.Cimd.HttpTimeout must be greater than zero.");
        }

        if (options.ClientRegistration.Cimd.MaxMetadataBytes <= 0)
        {
            errors.Add("AuthServer.ClientRegistration.Cimd.MaxMetadataBytes must be greater than zero.");
        }

        if (options.ClientRegistration.Dcr.RateLimitWindow <= TimeSpan.Zero)
        {
            errors.Add("AuthServer.ClientRegistration.Dcr.RateLimitWindow must be greater than zero.");
        }

        if (options.ClientRegistration.Dcr.MaxRegistrationsPerWindow <= 0)
        {
            errors.Add("AuthServer.ClientRegistration.Dcr.MaxRegistrationsPerWindow must be greater than zero.");
        }

        if (options.ClientRegistration.Dcr.StaleClientRetention <= TimeSpan.Zero)
        {
            errors.Add("AuthServer.ClientRegistration.Dcr.StaleClientRetention must be greater than zero.");
        }
    }

    private static void ValidateEmailOtpOptions(SqlOSEmailOtpOptions options, List<string> errors)
    {
        var hasConnectionString = !string.IsNullOrWhiteSpace(options.AzureCommunicationServicesConnectionString);
        var hasFromAddress = !string.IsNullOrWhiteSpace(options.FromAddress);

        if (hasConnectionString != hasFromAddress)
        {
            errors.Add("AuthServer.EmailOtp requires both AzureCommunicationServicesConnectionString and FromAddress when either is set.");
        }

        if (options.CodeLength < 4 || options.CodeLength > 8)
        {
            errors.Add("AuthServer.EmailOtp.CodeLength must be between 4 and 8 digits.");
        }

        if (options.ChallengeLifetime <= TimeSpan.Zero)
        {
            errors.Add("AuthServer.EmailOtp.ChallengeLifetime must be greater than zero.");
        }

        if (options.ResendCooldown < TimeSpan.Zero)
        {
            errors.Add("AuthServer.EmailOtp.ResendCooldown must be zero or greater.");
        }

        if (options.MaxAttempts <= 0)
        {
            errors.Add("AuthServer.EmailOtp.MaxAttempts must be greater than zero.");
        }

        if (options.MaxChallengesPerHour <= 0)
        {
            errors.Add("AuthServer.EmailOtp.MaxChallengesPerHour must be greater than zero.");
        }

        if (options.MaxChallengesPerIpPerHour <= 0)
        {
            errors.Add("AuthServer.EmailOtp.MaxChallengesPerIpPerHour must be greater than zero.");
        }

        if (options.MaxChallengesPerClientPerHour <= 0)
        {
            errors.Add("AuthServer.EmailOtp.MaxChallengesPerClientPerHour must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(options.ApplicationName))
        {
            errors.Add("AuthServer.EmailOtp.ApplicationName is required.");
        }
    }

    private static string BuildAuthBasePath(string dashboardBasePath)
        => dashboardBasePath == "/"
            ? "/auth"
            : $"{dashboardBasePath}/auth";

    private static string? ValidateRootPath(string? value, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name} is required.");
            return null;
        }

        if (value.Contains('?') || value.Contains('#'))
        {
            errors.Add($"{name} must be a path only, without query string or fragment.");
            return null;
        }

        var normalized = value.Trim();
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            errors.Add($"{name} must start with '/'.");
            return null;
        }

        return NormalizeRootPath(normalized);
    }

    private static Uri? ValidateAbsoluteUri(string? value, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name} is required.");
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            errors.Add($"{name} must be an absolute URI.");
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{name} must use http or https.");
            return null;
        }

        return uri;
    }

    private static string NormalizeRootPath(string path)
    {
        var trimmed = path.Trim();
        if (string.Equals(trimmed, "/", StringComparison.Ordinal))
        {
            return "/";
        }

        return trimmed.TrimEnd('/');
    }
}
