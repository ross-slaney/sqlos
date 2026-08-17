using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Security;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSOtpAdminServiceTests
{
    [TestMethod]
    public async Task ReadinessAndTestDelivery_AreRedactedAndCreateNoAuthenticationState()
    {
        await using var context = CreateContext();
        var optionsValue = ReadyOptions();
        var options = Options.Create(optionsValue);
        var email = new TestAuthEmailSender { IsConfigured = true };
        var phone = new FakePhoneChannel();
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var settings = new SqlOSSettingsService(context, options, email, crypto);
        await settings.UpsertSeededAuthPageSettingsAsync();
        var service = CreateService(context, admin, crypto, settings, email, phone, options);

        var readiness = await service.GetReadinessAsync();
        readiness.Email.Enabled.Should().BeTrue();
        readiness.Email.LocallyConfigured.Should().BeTrue();
        readiness.Phone.Enabled.Should().BeTrue();
        readiness.Phone.LocallyConfigured.Should().BeTrue();
        var serialized = JsonSerializer.Serialize(readiness);
        serialized.Should().NotContain("super-secret-email-connection");
        serialized.Should().NotContain("super-secret-twilio-token");

        var emailResult = await service.SendTestAsync("email", "operator@example.test", "127.0.0.1");
        var phoneResult = await service.SendTestAsync("phone", "+14155550123", "127.0.0.1");

        emailResult.MaskedDestination.Should().Be("o***@example.test");
        phoneResult.MaskedDestination.Should().EndWith("0123");
        email.Messages.Should().ContainSingle().Which.TextBody.Should().NotContain("code:");
        phone.Starts.Should().ContainSingle().Which.Context.Purpose.Should().Be("admin_test");
        (await context.Set<SqlOSUser>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSSession>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSEmailOtpChallenge>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSPhoneOtpChallenge>().CountAsync()).Should().Be(0);
        var audits = await context.Set<SqlOSAuditEvent>().Where(x => x.EventType == "otp.admin_test.succeeded").ToListAsync();
        audits.Should().HaveCount(2);
        JsonSerializer.Serialize(audits).Should().NotContain("operator@example.test");
        JsonSerializer.Serialize(audits).Should().NotContain("+14155550123");
    }

    [TestMethod]
    public async Task TestDelivery_UsesGenericProviderFailureAndDistributedRateLimit()
    {
        await using var context = CreateContext();
        var optionsValue = ReadyOptions();
        var options = Options.Create(optionsValue);
        var email = new TestAuthEmailSender { IsConfigured = true };
        var phone = new FakePhoneChannel { Reject = true };
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var settings = new SqlOSSettingsService(context, options, email, crypto);
        await settings.UpsertSeededAuthPageSettingsAsync();
        var limiter = new SqlOSOtpAdminRateLimiter(new SqlOSInMemoryRateLimitStore());
        var service = new SqlOSOtpAdminService(context, admin, crypto, settings, email, phone, limiter, options);

        await FluentActions.Invoking(() => service.SendTestAsync("phone", "+14155550123", null))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not complete*");
        phone.Reject = false;
        await service.SendTestAsync("phone", "+14155550123", null);
        await service.SendTestAsync("phone", "+14155550123", null);
        await FluentActions.Invoking(() => service.SendTestAsync("phone", "+14155550123", null))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*limit reached*");
    }

    [TestMethod]
    public async Task Readiness_ExplainsIncompleteConfigurationWithoutSecrets()
    {
        await using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedAuthPage(page => page.EnabledCredentialTypes = ["email_otp", "phone_otp"]);
        var options = Options.Create(optionsValue);
        var email = new TestAuthEmailSender { IsConfigured = false };
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var settings = new SqlOSSettingsService(context, options, email, crypto);
        await settings.UpsertSeededAuthPageSettingsAsync();
        var service = CreateService(context, admin, crypto, settings, email, new FakePhoneChannel(), options);

        var readiness = await service.GetReadinessAsync();

        readiness.Email.ReasonCodes.Should().Contain("email_sender_unavailable");
        readiness.Phone.ReasonCodes.Should().Contain(["method_disabled_in_host_configuration", "missing_auth_token", "missing_verify_service_sid"]);
    }

    private static SqlOSOtpAdminService CreateService(
        TestSqlOSInMemoryDbContext context,
        SqlOSAdminService admin,
        SqlOSCryptoService crypto,
        SqlOSSettingsService settings,
        ISqlOSAuthEmailSender email,
        ISqlOSOtpDeliveryChannel phone,
        IOptions<SqlOSAuthServerOptions> options)
        => new(context, admin, crypto, settings, email, phone,
            new SqlOSOtpAdminRateLimiter(new SqlOSInMemoryRateLimitStore()), options);

    private static SqlOSAuthServerOptions ReadyOptions()
    {
        var options = new SqlOSAuthServerOptions();
        options.SeedAuthPage(page => page.EnabledCredentialTypes = ["email_otp", "phone_otp"]);
        options.EmailOtp.AzureCommunicationServicesConnectionString = "super-secret-email-connection";
        options.EmailOtp.FromAddress = "signin@example.test";
        options.ConfigurePhoneOtp(phone =>
        {
            phone.Enabled = true;
            phone.TwilioAccountSid = "AC00000000000000000000000000000000";
            phone.TwilioAuthToken = "super-secret-twilio-token";
            phone.TwilioVerifyServiceSid = "VA00000000000000000000000000000000";
        });
        return options;
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
        => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class FakePhoneChannel : ISqlOSOtpDeliveryChannel
    {
        public bool Reject { get; set; }
        public List<(string Phone, SqlOSOtpDeliveryContext Context)> Starts { get; } = [];

        public Task<SqlOSOtpDeliveryStartResult> StartAsync(string e164PhoneNumber, SqlOSOtpDeliveryContext context, CancellationToken cancellationToken = default)
        {
            Starts.Add((e164PhoneNumber, context));
            return Task.FromResult(new SqlOSOtpDeliveryStartResult(!Reject, "fake_twilio", null, Reject ? "rejected" : "pending", Reject ? "sensitive provider detail" : null));
        }

        public Task<SqlOSOtpDeliveryCheckResult> CheckAsync(string e164PhoneNumber, string code, SqlOSOtpDeliveryContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new SqlOSOtpDeliveryCheckResult(false, "fake_twilio", null, "unused"));
    }
}
