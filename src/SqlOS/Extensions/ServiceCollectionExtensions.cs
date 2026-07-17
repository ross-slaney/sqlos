using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqlOS.AuditLogs;
using SqlOS.Configuration;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Services;
using SqlOS.AuthServer.Security;
using SqlOS.Calendar.Interfaces;
using SqlOS.Calendar.Services;
using SqlOS.Dashboard;
using SqlOS.Email.Configuration;
using SqlOS.Email.Interfaces;
using SqlOS.Email.Services;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Services;
using SqlOS.Hosting;
using SqlOS.Services;
using SqlOS.Security;

namespace SqlOS.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSqlOS<TContext>(
        this IServiceCollection services,
        Action<SqlOSOptions>? configure = null)
        where TContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
    {
        var options = new SqlOSOptions();
        configure?.Invoke(options);

        SqlOSPathDefaults.Apply(options);
        options.AuthServer.Dashboard = options.Dashboard;
        options.Fga.Dashboard = options.Dashboard;
        SqlOSOptionsValidator.ValidateOrThrow(options);

        services.AddSingleton(Options.Create(options));
        services.AddSingleton(Options.Create(options.AuthServer));
        services.AddSingleton(Options.Create(options.Fga));
        services.AddSingleton(Options.Create(options.Email));
        services.AddSingleton(Options.Create(options.Calendar));
        services.AddDataProtection();
        services.AddSingleton<SqlOSHostedFormAntiforgery>();
        services.AddSingleton<SqlOSBrowserSecurityHeaders>();
        services.AddHttpClient();
        services.AddHttpClient(nameof(SqlOSOidcAuthService), client =>
        {
            client.Timeout = SqlOSOidcAuthService.ProviderHttpTimeout;
        })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false
            });
        services.AddHttpClient(nameof(SqlOSCimdClientService))
            .ConfigurePrimaryHttpMessageHandler(SqlOSCimdHttpHandlerFactory.Create);
        services.AddHttpClient<ISqlOSDomainDnsVerifier, SqlOSDnsOverHttpsDomainVerifier>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddSingleton<SqlOSDashboardSessionService>();

        services.AddScoped<ISqlOSAuthServerDbContext>(sp => sp.GetRequiredService<TContext>());
        services.AddScoped<ISqlOSFgaDbContext>(sp => sp.GetRequiredService<TContext>());
        services.AddScoped<SqlOSDistributedRateLimitStore>();
        services.AddScoped(sp => new SqlOSDashboardLoginThrottlingService(
            sp.GetRequiredService<SqlOSDistributedRateLimitStore>()));
        services.AddScoped(sp => new SqlOSDynamicClientRegistrationRateLimiter(
            sp.GetRequiredService<SqlOSDistributedRateLimitStore>()));

        services.AddScoped<SqlOSSchemaInitializer>();
        services.AddScoped<SqlOSBootstrapper>();
        services.AddSingleton<SqlOSValidationSigningKeyCache>();
        services.AddScoped(sp =>
        {
            var context = sp.GetRequiredService<ISqlOSAuthServerDbContext>();
            var authOptions = sp.GetRequiredService<IOptions<SqlOSAuthServerOptions>>();
            var dataProtection = sp.GetRequiredService<IDataProtectionProvider>();
            var cache = sp.GetRequiredService<SqlOSValidationSigningKeyCache>();
            return new SqlOSCryptoService(
                context,
                authOptions,
                new SqlOSDataProtectionSigningKeyCustody(dataProtection),
                dataProtection,
                cache);
        });
        services.AddScoped<ISqlOSAuditLogService, SqlOSAuditLogService>();
        services.AddScoped<SqlOSSettingsService>();
        services.AddSingleton<ISqlOSAuthEmailSender, SqlOSAcsAuthEmailSender>();
        services.AddSingleton<SqlOSAcsEmailSender>();
        services.AddSingleton<ISqlOSEmailSender, SqlOSDefaultEmailSender>();
        services.AddSingleton<SqlOSEmailTemplateRenderer>();
        services.AddScoped<ISqlOSTransactionalEmailService, SqlOSTransactionalEmailService>();
        services.AddScoped<SqlOSEmailAdminService>();
        services.AddScoped<SqlOSEmailOtpService>();
        services.AddScoped<SqlOSMagicLinkService>();
        services.AddSingleton<ISqlOSOtpDeliveryChannel, SqlOSTwilioVerifyOtpChannel>();
        services.AddScoped<SqlOSPhoneOtpService>();
        services.AddScoped(sp => new SqlOSOtpAdminRateLimiter(
            sp.GetRequiredService<SqlOSDistributedRateLimitStore>()));
        services.AddScoped<SqlOSOtpAdminService>();
        services.AddScoped<SqlOSMfaPolicyService>();
        services.AddScoped<SqlOSTotpMfaService>();
        services.AddScoped<SqlOSPasswordLoginAbuseService>();
        services.AddScoped<SqlOSInvitationService>();
        services.AddScoped<SqlOSDeviceAuthorizationService>();
        services.AddScoped<SqlOSClientCredentialsService>();
        services.AddScoped<SqlOSMachineClientAdminService>();
        services.AddScoped<SqlOSCimdClientService>();
        services.AddScoped<SqlOSDynamicClientRegistrationService>();
        services.AddScoped<SqlOSClientResolutionService>();
        services.AddScoped<SqlOSAdminService>();
        services.AddScoped<SqlOSSessionRevocationService>();
        services.AddScoped<SqlOSAuthService>();
        services.AddScoped<SqlOSAuthPageSessionService>();
        services.AddScoped<SqlOSAuthorizationServerService>();
        services.AddScoped<SqlOSHeadlessAuthService>();
        services.AddScoped<SqlOSHomeRealmDiscoveryService>();
        services.AddScoped<SqlOSOidcAuthService>();
        services.AddScoped<SqlOSOidcBrowserAuthService>();
        services.AddScoped<SqlOSSamlService>();
        services.AddScoped<SqlOSSsoAuthorizationService>();
        services.AddScoped<SqlOSOrganizationDomainService>();
        services.AddScoped<SqlOSSsoPortalService>();
        services.AddSingleton<ISqlOSCalendarProviderAdapter, SqlOSGoogleCalendarAdapter>();
        services.AddSingleton<ISqlOSCalendarProviderAdapter, SqlOSMicrosoftGraphCalendarAdapter>();
        services.AddScoped<SqlOSCalendarService>();
        services.AddScoped<SqlOSCalendarSyncService>();
        services.AddScoped<SqlOSScimService>();
        services.AddScoped<ISqlOSFgaAuthService, SqlOSFgaAuthService>();
        services.AddScoped<ISqlOSFgaSubjectService, SqlOSFgaSubjectService>();
        services.AddScoped<ISpecificationExecutor, SpecificationExecutor>();
        services.AddScoped<SqlOSFgaSeedService>();
        services.AddScoped<SqlOSFgaFunctionInitializer>();
        services.AddScoped<SqlOSFgaSchemaInitializer>();
        services.AddScoped<SqlOSFgaHierarchyValidator>();
        services.AddHostedService<SqlOSSigningKeyRotationService>();
        services.AddHostedService<SqlOSCalendarSyncHostedService>();
        services.AddHostedService<SqlOSBootstrapHostedService>();
        services.AddSingleton<IStartupFilter, SqlOSPipelineStartupFilter>();

        return services;
    }
}
