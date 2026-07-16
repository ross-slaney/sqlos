using SqlOS.AuthServer.Configuration;
using SqlOS.Calendar.Configuration;
using SqlOS.Email.Configuration;
using SqlOS.Fga;

namespace SqlOS.Configuration;

internal static class SqlOSOptionsValidator
{
    public static void ValidateOrThrow(SqlOSOptions options)
    {
        var errors = new List<string>();

        var dashboardBasePath = ValidateRootPath(options.DashboardBasePath, nameof(options.DashboardBasePath), errors);
        var authBasePath = ValidateRootPath(options.AuthServer.BasePath, "AuthServer.BasePath", errors);
        string? scimBasePath = null;
        if (options.AuthServer.EnableScim)
        {
            scimBasePath = ValidateRootPath(options.AuthServer.ScimBasePath, "AuthServer.ScimBasePath", errors);
            if (scimBasePath == "/")
            {
                errors.Add("AuthServer.ScimBasePath cannot be '/'.");
            }
        }

        if (dashboardBasePath != null && authBasePath != null)
        {
            var expectedAuthBasePath = BuildAuthBasePath(dashboardBasePath);
            if (!string.Equals(authBasePath, expectedAuthBasePath, StringComparison.Ordinal))
            {
                errors.Add($"AuthServer.BasePath must be '{expectedAuthBasePath}' when DashboardBasePath is '{dashboardBasePath}'.");
            }
        }

        if (scimBasePath != null && dashboardBasePath != null && authBasePath != null)
        {
            var adminBasePath = dashboardBasePath == "/" ? "/admin/auth" : $"{dashboardBasePath}/admin/auth";
            if (string.Equals(scimBasePath, dashboardBasePath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(scimBasePath, authBasePath, StringComparison.OrdinalIgnoreCase)
                || scimBasePath.StartsWith(authBasePath + "/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(scimBasePath, adminBasePath, StringComparison.OrdinalIgnoreCase)
                || scimBasePath.StartsWith(adminBasePath + "/", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("AuthServer.ScimBasePath must not overlap the dashboard, auth server, or admin API root.");
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
        ValidateBrowserSecurityOptions(options.BrowserSecurity, errors);
        if (options.Fga.MaxResourceHierarchyDepth <= 0)
        {
            errors.Add("Fga.MaxResourceHierarchyDepth must be greater than zero.");
        }
        else if (options.Fga.MaxResourceHierarchyDepth > SqlOSFgaHierarchyDepth.SqlServerRecursiveCteMaximum)
        {
            errors.Add(
                $"Fga.MaxResourceHierarchyDepth cannot exceed {SqlOSFgaHierarchyDepth.SqlServerRecursiveCteMaximum}, "
                + "the SQL Server recursive CTE limit used by FGA list authorization.");
        }

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
        ValidateSsoPortalOptions(options.AuthServer.SsoPortal, errors);
        ValidateEmailOtpOptions(options.AuthServer.EmailOtp, errors);
        ValidateMagicLinkOptions(options.AuthServer.MagicLink, errors);
        ValidatePhoneOtpOptions(options.AuthServer.PhoneOtp, errors);
        ValidatePasswordResetOptions(options.AuthServer.PasswordReset, errors);
        ValidateEmailOptions(options.Email, errors);
        ValidateCalendarOptions(options.Calendar, errors);
        ValidatePasswordLoginAbuseOptions(options.AuthServer.PasswordLogin, errors);
        ValidateMfaOptions(options.AuthServer.Mfa, errors);
        ValidateAccessTokenValidationOptions(options.AuthServer, errors);
        ValidateClientRegistrationOptions(options.AuthServer, errors);
        ValidateSigningKeyOptions(options.AuthServer, errors);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid SqlOS configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(static error => $"- {error}")));
        }
    }

    private static void ValidateBrowserSecurityOptions(
        SqlOSBrowserSecurityOptions options,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(options.ContentSecurityPolicy))
        {
            errors.Add("BrowserSecurity.ContentSecurityPolicy cannot be empty.");
            return;
        }

        if (options.ContentSecurityPolicy.Contains('\r') || options.ContentSecurityPolicy.Contains('\n'))
        {
            errors.Add("BrowserSecurity.ContentSecurityPolicy must be a single header-safe line.");
        }

        var scriptDirective = FindCspDirective(options.ContentSecurityPolicy, "script-src");
        var styleDirective = FindCspDirective(options.ContentSecurityPolicy, "style-src");
        if (scriptDirective == null
            || !scriptDirective.Contains(SqlOSBrowserSecurityOptions.NoncePlaceholder, StringComparison.Ordinal))
        {
            errors.Add("BrowserSecurity.ContentSecurityPolicy script-src must include the {nonce} placeholder.");
        }

        if (styleDirective == null
            || !styleDirective.Contains(SqlOSBrowserSecurityOptions.NoncePlaceholder, StringComparison.Ordinal))
        {
            errors.Add("BrowserSecurity.ContentSecurityPolicy style-src must include the {nonce} placeholder.");
        }

        if (scriptDirective != null
            && (scriptDirective.Contains("'unsafe-inline'", StringComparison.OrdinalIgnoreCase)
                || scriptDirective.Contains("'unsafe-eval'", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("BrowserSecurity.ContentSecurityPolicy script-src cannot allow unsafe-inline or unsafe-eval.");
        }

        if (styleDirective != null
            && styleDirective.Contains("'unsafe-inline'", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("BrowserSecurity.ContentSecurityPolicy style-src cannot allow unsafe-inline.");
        }

        if (options.ContentSecurityPolicy.Contains("frame-ancestors", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("BrowserSecurity.ContentSecurityPolicy cannot configure frame-ancestors; SqlOS always enforces frame-ancestors 'none'.");
        }
    }

    private static string? FindCspDirective(string policy, string directiveName)
        => policy.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(directive =>
                directive.Equals(directiveName, StringComparison.OrdinalIgnoreCase)
                || directive.StartsWith(directiveName + " ", StringComparison.OrdinalIgnoreCase));

    private static void ValidateMfaOptions(SqlOSMfaOptions options, List<string> errors)
    {
        if (options.Totp.MaxFailedAttemptsPerChallenge <= 0)
        {
            errors.Add("AuthServer.Mfa.Totp.MaxFailedAttemptsPerChallenge must be greater than zero.");
        }

        if (options.Totp.MaxFailedAttemptsPerUser < options.Totp.MaxFailedAttemptsPerChallenge)
        {
            errors.Add("AuthServer.Mfa.Totp.MaxFailedAttemptsPerUser must be at least MaxFailedAttemptsPerChallenge.");
        }

        if (options.Totp.MaxFailedAttemptsPerIp < options.Totp.MaxFailedAttemptsPerChallenge)
        {
            errors.Add("AuthServer.Mfa.Totp.MaxFailedAttemptsPerIp must be at least MaxFailedAttemptsPerChallenge.");
        }

        if (options.Totp.FailedAttemptWindow <= TimeSpan.Zero)
        {
            errors.Add("AuthServer.Mfa.Totp.FailedAttemptWindow must be greater than zero.");
        }
    }

    private static void ValidateSigningKeyOptions(SqlOSAuthServerOptions options, List<string> errors)
    {
        if (options.DefaultSigningKeyRotationIntervalDays <= 0
            || options.DefaultSigningKeyGraceWindowDays <= 0
            || options.DefaultSigningKeyRetiredCleanupDays <= 0)
        {
            errors.Add("AuthServer default signing-key lifecycle values must be positive days.");
        }

        if (options.DefaultSigningKeyGraceWindowDays >= options.DefaultSigningKeyRotationIntervalDays)
        {
            errors.Add("AuthServer.DefaultSigningKeyGraceWindowDays must be shorter than DefaultSigningKeyRotationIntervalDays.");
        }

        if (options.DefaultSigningKeyRetiredCleanupDays < options.DefaultSigningKeyGraceWindowDays)
        {
            errors.Add("AuthServer.DefaultSigningKeyRetiredCleanupDays must be at least DefaultSigningKeyGraceWindowDays.");
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

    private static void ValidateSsoPortalOptions(SqlOSSsoPortalOptions options, List<string> errors)
    {
        if (options.DefaultLinkLifetime <= TimeSpan.Zero)
        {
            errors.Add("AuthServer.SsoPortal.DefaultLinkLifetime must be greater than zero.");
        }

        if (options.SessionIdleTimeout <= TimeSpan.Zero)
        {
            errors.Add("AuthServer.SsoPortal.SessionIdleTimeout must be greater than zero.");
        }

        if (!string.IsNullOrWhiteSpace(options.HeadlessApiBasePath))
        {
            ValidateRootPath(options.HeadlessApiBasePath, "AuthServer.SsoPortal.HeadlessApiBasePath", errors);
            if (!options.EnableApi)
            {
                errors.Add("AuthServer.SsoPortal.HeadlessApiBasePath requires AuthServer.SsoPortal.EnableApi.");
            }
        }

        if (options.BuildUiUrl != null && !options.EnableApi)
        {
            errors.Add("AuthServer.SsoPortal.BuildUiUrl requires AuthServer.SsoPortal.EnableApi.");
        }

        if (string.IsNullOrWhiteSpace(options.DomainVerificationRecordPrefix))
        {
            errors.Add("AuthServer.SsoPortal.DomainVerificationRecordPrefix is required.");
        }

        if (string.IsNullOrWhiteSpace(options.DomainVerificationRecordValuePrefix))
        {
            errors.Add("AuthServer.SsoPortal.DomainVerificationRecordValuePrefix is required.");
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

    private static void ValidateAccessTokenValidationOptions(SqlOSAuthServerOptions options, List<string> errors)
    {
        if (options.AccessTokenValidationSigningKeyCacheTtl < TimeSpan.Zero)
        {
            errors.Add("AuthServer.AccessTokenValidationSigningKeyCacheTtl must be zero or greater.");
        }

        if (options.AccessTokenValidationLastSeenDebounceInterval < TimeSpan.Zero)
        {
            errors.Add("AuthServer.AccessTokenValidationLastSeenDebounceInterval must be zero or greater.");
        }
    }

    private static void ValidatePasswordResetOptions(SqlOSPasswordResetOptions options, List<string> errors)
    {
        if (options.TokenLifetime <= TimeSpan.Zero)
        {
            errors.Add("AuthServer.PasswordReset.TokenLifetime must be greater than zero.");
        }

        if (options.ResendCooldown <= TimeSpan.Zero)
        {
            errors.Add("AuthServer.PasswordReset.ResendCooldown must be greater than zero.");
        }

        if (options.RateLimitWindow <= TimeSpan.Zero)
        {
            errors.Add("AuthServer.PasswordReset.RateLimitWindow must be greater than zero.");
        }

        if (options.MaxRequestsPerEmailPerWindow <= 0)
        {
            errors.Add("AuthServer.PasswordReset.MaxRequestsPerEmailPerWindow must be greater than zero.");
        }

        if (options.MaxRequestsPerIpPerWindow <= 0)
        {
            errors.Add("AuthServer.PasswordReset.MaxRequestsPerIpPerWindow must be greater than zero.");
        }

        if (options.MaxRequestsPerClientPerWindow <= 0)
        {
            errors.Add("AuthServer.PasswordReset.MaxRequestsPerClientPerWindow must be greater than zero.");
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

    private static void ValidateMagicLinkOptions(SqlOSMagicLinkOptions options, List<string> errors)
    {
        if (options.TokenLifetime <= TimeSpan.Zero)
        {
            errors.Add("AuthServer.MagicLink.TokenLifetime must be greater than zero.");
        }

        if (options.ResendCooldown < TimeSpan.Zero)
        {
            errors.Add("AuthServer.MagicLink.ResendCooldown must be zero or greater.");
        }

        if (options.RateLimitWindow <= TimeSpan.Zero)
        {
            errors.Add("AuthServer.MagicLink.RateLimitWindow must be greater than zero.");
        }

        if (options.MaxLinksPerEmailPerWindow <= 0)
        {
            errors.Add("AuthServer.MagicLink.MaxLinksPerEmailPerWindow must be greater than zero.");
        }

        if (options.MaxLinksPerIpPerWindow <= 0)
        {
            errors.Add("AuthServer.MagicLink.MaxLinksPerIpPerWindow must be greater than zero.");
        }

        if (options.MaxLinksPerClientPerWindow <= 0)
        {
            errors.Add("AuthServer.MagicLink.MaxLinksPerClientPerWindow must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(options.ApplicationName))
        {
            errors.Add("AuthServer.MagicLink.ApplicationName is required.");
        }
    }

    private static void ValidatePhoneOtpOptions(SqlOSPhoneOtpOptions options, List<string> errors)
    {
        if (options.Enabled && !options.HasCompleteTwilioConfiguration)
        {
            errors.Add("AuthServer.PhoneOtp requires TwilioAccountSid, TwilioAuthToken, and TwilioVerifyServiceSid when Enabled is true.");
        }

        if (options.ChallengeLifetime <= TimeSpan.Zero)
        {
            errors.Add("AuthServer.PhoneOtp.ChallengeLifetime must be greater than zero.");
        }

        if (options.ResendCooldown < TimeSpan.Zero)
        {
            errors.Add("AuthServer.PhoneOtp.ResendCooldown must be zero or greater.");
        }

        if (options.RateLimitWindow <= TimeSpan.Zero)
        {
            errors.Add("AuthServer.PhoneOtp.RateLimitWindow must be greater than zero.");
        }

        if (options.MaxSendsPerPhone <= 0)
        {
            errors.Add("AuthServer.PhoneOtp.MaxSendsPerPhone must be greater than zero.");
        }

        if (options.MaxSendsPerAccount <= 0)
        {
            errors.Add("AuthServer.PhoneOtp.MaxSendsPerAccount must be greater than zero.");
        }

        if (options.MaxSendsPerIp <= 0)
        {
            errors.Add("AuthServer.PhoneOtp.MaxSendsPerIp must be greater than zero.");
        }

        if (options.MaxSendsPerClient <= 0)
        {
            errors.Add("AuthServer.PhoneOtp.MaxSendsPerClient must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(options.DefaultRegion))
        {
            errors.Add("AuthServer.PhoneOtp.DefaultRegion is required.");
        }

        ValidateCountryList(options.CountryAllowList, "AuthServer.PhoneOtp.CountryAllowList", errors);
        ValidateCountryList(options.CountryDenyList, "AuthServer.PhoneOtp.CountryDenyList", errors);
    }

    private static void ValidateCountryList(IEnumerable<string>? values, string name, List<string> errors)
    {
        foreach (var value in values ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (trimmed.Length != 2 || !trimmed.All(char.IsLetter))
            {
                errors.Add($"{name} values must be ISO 3166-1 alpha-2 country codes.");
                return;
            }
        }
    }

    private static void ValidateEmailOptions(SqlOSEmailOptions options, List<string> errors)
    {
        var hasConnectionString = !string.IsNullOrWhiteSpace(options.AzureCommunicationServicesConnectionString);
        var hasFromAddress = !string.IsNullOrWhiteSpace(options.FromAddress);

        if (hasConnectionString != hasFromAddress)
        {
            errors.Add("Email requires both AzureCommunicationServicesConnectionString and FromAddress when either is set.");
        }

        if (options.DeliveryRetention <= TimeSpan.Zero)
        {
            errors.Add("Email.DeliveryRetention must be greater than zero.");
        }

        if (options.RenderedTextPreviewMaxLength <= 0)
        {
            errors.Add("Email.RenderedTextPreviewMaxLength must be greater than zero.");
        }
    }

    private static void ValidateCalendarOptions(SqlOSCalendarOptions options, List<string> errors)
    {
        if (options.ConnectSessionLifetime <= TimeSpan.Zero)
        {
            errors.Add("Calendar.ConnectSessionLifetime must be greater than zero.");
        }

        if (options.AccessTokenRefreshSkew < TimeSpan.Zero)
        {
            errors.Add("Calendar.AccessTokenRefreshSkew must be zero or greater.");
        }

        if (options.SyncWindowPastDays < 0)
        {
            errors.Add("Calendar.SyncWindowPastDays must be zero or greater.");
        }

        if (options.SyncWindowFutureDays <= 0)
        {
            errors.Add("Calendar.SyncWindowFutureDays must be greater than zero.");
        }

        if (!options.SyncScheduler.Enabled)
        {
            return;
        }

        if (options.SyncScheduler.Interval <= TimeSpan.Zero)
        {
            errors.Add("Calendar.SyncScheduler.Interval must be greater than zero.");
        }

        if (options.SyncScheduler.SyncEvery <= TimeSpan.Zero)
        {
            errors.Add("Calendar.SyncScheduler.SyncEvery must be greater than zero.");
        }

        if (options.SyncScheduler.MaxConnectionsPerRun <= 0)
        {
            errors.Add("Calendar.SyncScheduler.MaxConnectionsPerRun must be greater than zero.");
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

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            errors.Add($"{name} cannot include user information.");
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
