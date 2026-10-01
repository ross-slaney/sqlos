using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Processes.Identity;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;
using SqlOS.Email.Configuration;
using SqlOS.Email.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests.Processes;

/// <summary>
/// The identity processes on the EF Core in-memory provider, built as the public facades build them
/// (<see cref="SqlOSIdentityProcesses"/>), with every channel the recovery, verification and MFA
/// processes use and an email sender that keeps what it sent.
/// </summary>
internal sealed class IdentityProcessHarness
{
    public const string ClientId = "process-client";
    public const string ThirdPartyClientId = "process-partner";
    public const string Password = "P@ssword123!";
    public const string IpAddress = "203.0.113.77";

    private IdentityProcessHarness(
        TestSqlOSInMemoryDbContext context,
        SqlOSAuthServerOptions options,
        TestAuthEmailSender emails,
        SqlOSCryptoService crypto,
        SqlOSAdminService admin,
        SqlOSSettingsService settings,
        SqlOSTotpMfaService authenticators,
        SqlOSAuthService auth,
        SqlOSAuthorizationServerService authorization)
    {
        Context = context;
        Options = options;
        Emails = emails;
        Crypto = crypto;
        Admin = admin;
        Settings = settings;
        Authenticators = authenticators;
        Auth = auth;
        Authorization = authorization;
    }

    public TestSqlOSInMemoryDbContext Context { get; }
    public SqlOSAuthServerOptions Options { get; }
    public TestAuthEmailSender Emails { get; }
    public SqlOSCryptoService Crypto { get; }
    public SqlOSAdminService Admin { get; }
    public SqlOSSettingsService Settings { get; }
    public SqlOSTotpMfaService Authenticators { get; }
    public SqlOSAuthService Auth { get; }
    public SqlOSAuthorizationServerService Authorization { get; }

    /// <summary>The request every process call in these tests serves.</summary>
    public static SqlOSRequestContext Request { get; } =
        new(SqlOSRequestSurface.PublicApi, IpAddress, "IdentityProcessTests", RequestId: null, CorrelationId: null, Route: "/sqlos/auth/test");

    public static async Task<IdentityProcessHarness> CreateAsync(Action<SqlOSAuthServerOptions>? configure = null)
    {
        var context = new TestSqlOSInMemoryDbContext(
            new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var authOptions = new SqlOSAuthServerOptions
        {
            Issuer = "https://auth.example.test/sqlos/auth",
            BasePath = "/sqlos/auth"
        };
        authOptions.SeedBrowserClient(ClientId, "Process Client", "https://client.example.test/callback");
        configure?.Invoke(authOptions);
        var options = Microsoft.Extensions.Options.Options.Create(authOptions);
        var crypto = TestCryptoService.Create(context, options, new EphemeralDataProtectionProvider());
        var admin = new SqlOSAdminService(context, options, crypto);
        var emails = new TestAuthEmailSender { IsConfigured = true };
        var settings = new SqlOSSettingsService(context, options, emails);
        var transactional = new SqlOSTransactionalEmailService(
            context,
            crypto,
            emails,
            new SqlOSEmailTemplateRenderer(),
            Microsoft.Extensions.Options.Options.Create(new SqlOSEmailOptions()));
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emails, options, transactional);
        var policy = new SqlOSMfaPolicyService(context, settings, options);
        var authenticators = new SqlOSTotpMfaService(context, crypto, policy, options);
        var auth = new SqlOSAuthService(
            context,
            options,
            admin,
            crypto,
            settings,
            emailOtp,
            transactionalEmailService: transactional,
            authEmailSender: emails,
            mfaPolicyService: policy,
            totpMfaService: authenticators);
        var authorization = new SqlOSAuthorizationServerService(
            context,
            admin,
            auth,
            crypto,
            settings,
            new SqlOSIssuerSessionService(context, crypto, settings),
            options,
            mfaPolicyService: policy,
            totpMfaService: authenticators);

        await crypto.EnsureActiveSigningKeyAsync();
        await admin.UpsertSeededClientsAsync();
        await settings.UpsertSeededAuthPageSettingsAsync();
        await settings.UpsertSeededAuthEmailSettingsAsync();
        await settings.UpsertSeededMfaSettingsAsync();
        await new SqlOSEmailAdminService(context, crypto, new SqlOSEmailTemplateRenderer()).EnsureBuiltInTemplatesAsync();
        await admin.CreateClientAsync(new SqlOSCreateClientRequest(
            ThirdPartyClientId,
            "Process Partner",
            "process-partner",
            ["https://partner.example.test/callback"],
            IsFirstParty: false));
        return new IdentityProcessHarness(context, authOptions, emails, crypto, admin, settings, authenticators, auth, authorization);
    }

    /// <summary>The processes of the public facades, completing logins through the hub adapter for <paramref name="httpContext"/>.</summary>
    public SqlOSIdentityProcesses Processes => new(Context, Admin, Crypto, Settings, Options)
    {
        PasswordResetAdmission = Auth.Admission,
        MfaAdmission = Auth.Admission,
        PasswordResetEmails = Auth.PasswordResetEmails,
        VerificationEmails = Auth.VerificationEmails,
        Authenticators = Authenticators,
        Auth = Auth,
        AuthorizationServer = Authorization
    };

    /// <summary>An operator-created account with <see cref="Password"/>; its address is unverified.</summary>
    public Task<SqlOSUser> CreateUserAsync(string? email = null)
        => Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Process User",
            email ?? $"process-{Guid.NewGuid():N}@example.com",
            Password));

    /// <summary>The audit rows of <paramref name="eventType"/>, oldest first.</summary>
    public Task<List<SqlOSAuditEvent>> AuditAsync(string eventType)
        => Context.Set<SqlOSAuditEvent>()
            .AsNoTracking()
            .Where(x => x.EventType == eventType)
            .OrderBy(x => x.OccurredAt)
            .ToListAsync();

    /// <summary>The token a link in the last email to <paramref name="email"/> carries.</summary>
    public string LinkTokenSentTo(string email)
    {
        var body = Emails.Messages.Last(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase)).TextBody ?? string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(body, @"token=([A-Za-z0-9_%-]+)");
        return match.Success
            ? Uri.UnescapeDataString(match.Groups[1].Value)
            : throw new InvalidOperationException($"No link was sent to {email}.");
    }

    public static DefaultHttpContext HttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("auth.example.test");
        context.Request.Headers.UserAgent = "IdentityProcessTests";
        context.Connection.RemoteIpAddress = IPAddress.Parse(IpAddress);
        return context;
    }
}
