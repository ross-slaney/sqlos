using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqlOS.AuditLogs;
using SqlOS.Configuration;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Services;
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
        services.AddHttpClient();
        services.AddHttpClient<ISqlOSDomainDnsVerifier, SqlOSDnsOverHttpsDomainVerifier>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddSingleton<SqlOSDashboardSessionService>();
        services.AddSingleton<SqlOSDashboardLoginThrottlingService>();
        services.AddSingleton<SqlOSDynamicClientRegistrationRateLimiter>();

        services.AddScoped<ISqlOSAuthServerDbContext>(sp => sp.GetRequiredService<TContext>());
        services.AddScoped<ISqlOSFgaDbContext>(sp => sp.GetRequiredService<TContext>());

        services.AddScoped<SqlOSSchemaInitializer>();
        services.AddScoped<SqlOSBootstrapper>();
        services.AddScoped<SqlOSCryptoService>();
        services.AddScoped<ISqlOSAuditLogService, SqlOSAuditLogService>();
        services.AddScoped<SqlOSSettingsService>();
        services.AddSingleton<ISqlOSAuthEmailSender, SqlOSAcsAuthEmailSender>();
        services.AddSingleton<SqlOSAcsEmailSender>();
        services.AddSingleton<ISqlOSEmailSender, SqlOSDefaultEmailSender>();
        services.AddSingleton<SqlOSEmailTemplateRenderer>();
        services.AddScoped<ISqlOSTransactionalEmailService, SqlOSTransactionalEmailService>();
        services.AddScoped<SqlOSEmailAdminService>();
        services.AddScoped<SqlOSEmailOtpService>();
        services.AddSingleton<ISqlOSOtpDeliveryChannel, SqlOSTwilioVerifyOtpChannel>();
        services.AddScoped<SqlOSPhoneOtpService>();
        services.AddScoped<SqlOSMfaPolicyService>();
        services.AddScoped<SqlOSTotpMfaService>();
        services.AddScoped<SqlOSPasswordLoginAbuseService>();
        services.AddScoped<SqlOSInvitationService>();
        services.AddScoped<SqlOSDeviceAuthorizationService>();
        services.AddScoped<SqlOSCimdClientService>();
        services.AddScoped<SqlOSDynamicClientRegistrationService>();
        services.AddScoped<SqlOSClientResolutionService>();
        services.AddScoped<SqlOSAdminService>();
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
        services.AddScoped<ISqlOSFgaAuthService, SqlOSFgaAuthService>();
        services.AddScoped<ISqlOSFgaSubjectService, SqlOSFgaSubjectService>();
        services.AddScoped<ISpecificationExecutor, SpecificationExecutor>();
        services.AddScoped<SqlOSFgaSeedService>();
        services.AddScoped<SqlOSFgaFunctionInitializer>();
        services.AddScoped<SqlOSFgaSchemaInitializer>();
        services.AddHostedService<SqlOSSigningKeyRotationService>();
        services.AddHostedService<SqlOSCalendarSyncHostedService>();
        services.AddHostedService<SqlOSBootstrapHostedService>();
        services.AddSingleton<IStartupFilter, SqlOSPipelineStartupFilter>();

        return services;
    }
}
