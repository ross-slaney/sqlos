using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSPhoneOtpServiceTests
{
    [TestMethod]
    public async Task PhoneOtp_Start_ReturnsGenericResponseForUnknownPhoneOrAccount()
    {
        using var harness = await PhoneOtpHarness.CreateAsync(options =>
        {
            options.PhoneOtp.ResendCooldown = TimeSpan.Zero;
        });

        var unknown = await harness.Service.StartForClientAsync(
            new SqlOSPhoneOtpStartRequest("+12025550100", "test-client", null),
            CreateHttpContext("203.0.113.10"));

        var user = await harness.CreateUserWithPhoneAsync("+12025550101");
        user.Should().NotBeNull();

        var known = await harness.Service.StartForClientAsync(
            new SqlOSPhoneOtpStartRequest("+12025550101", "test-client", null),
            CreateHttpContext("203.0.113.11"));

        unknown.Message.Should().Be(known.Message);
        unknown.Message.Should().Be("If an account exists for that phone number, check your messages for a sign-in code.");
        harness.Channel.StartRequests.Should().ContainSingle(x => x.PhoneNumber == "+12025550101");
    }

    [TestMethod]
    public async Task PhoneOtp_Start_NormalizesPhoneNumbersToE164()
    {
        using var harness = await PhoneOtpHarness.CreateAsync();
        await harness.CreateUserWithPhoneAsync("+12025550105");

        var start = await harness.Service.StartForClientAsync(
            new SqlOSPhoneOtpStartRequest("(202) 555-0105", "test-client", null),
            CreateHttpContext("203.0.113.12"));

        start.PhoneNumber.Should().Be("+12025550105");
        harness.Channel.StartRequests.Should().ContainSingle(x => x.PhoneNumber == "+12025550105");
    }

    [TestMethod]
    public async Task PhoneOtp_Start_RateLimitsByPhoneAccountIpAndClient()
    {
        using var byPhone = await PhoneOtpHarness.CreateAsync(options =>
        {
            options.PhoneOtp.ResendCooldown = TimeSpan.Zero;
            options.PhoneOtp.MaxSendsPerPhone = 1;
        });
        await byPhone.CreateUserWithPhoneAsync("+12025550110");
        await byPhone.Service.StartForClientAsync(new SqlOSPhoneOtpStartRequest("+12025550110", "test-client", null));
        var phoneAct = async () => await byPhone.Service.StartForClientAsync(new SqlOSPhoneOtpStartRequest("+12025550110", "test-client", null));
        await phoneAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Too many sign-in code requests. Try again later.");
        byPhone.Channel.StartRequests.Should().HaveCount(1);

        using var byAccount = await PhoneOtpHarness.CreateAsync(options =>
        {
            options.PhoneOtp.ResendCooldown = TimeSpan.Zero;
            options.PhoneOtp.MaxSendsPerAccount = 1;
        });
        var accountUser = await byAccount.CreateUserWithPhoneAsync("+12025550111");
        await byAccount.Service.AddVerifiedPhoneNumberAsync(accountUser, "+12025550112");
        await byAccount.Service.StartForClientAsync(new SqlOSPhoneOtpStartRequest("+12025550111", "test-client", null));
        var accountAct = async () => await byAccount.Service.StartForClientAsync(new SqlOSPhoneOtpStartRequest("+12025550112", "test-client", null));
        await accountAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Too many sign-in code requests. Try again later.");

        using var byIp = await PhoneOtpHarness.CreateAsync(options =>
        {
            options.PhoneOtp.ResendCooldown = TimeSpan.Zero;
            options.PhoneOtp.MaxSendsPerIp = 1;
        });
        await byIp.CreateUserWithPhoneAsync("+12025550113");
        await byIp.CreateUserWithPhoneAsync("+12025550114");
        await byIp.Service.StartForClientAsync(
            new SqlOSPhoneOtpStartRequest("+12025550113", "test-client", null),
            CreateHttpContext("203.0.113.20"));
        var ipAct = async () => await byIp.Service.StartForClientAsync(
            new SqlOSPhoneOtpStartRequest("+12025550114", "test-client", null),
            CreateHttpContext("203.0.113.20"));
        await ipAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Too many sign-in code requests. Try again later.");

        using var byClient = await PhoneOtpHarness.CreateAsync(options =>
        {
            options.PhoneOtp.ResendCooldown = TimeSpan.Zero;
            options.PhoneOtp.MaxSendsPerClient = 1;
        });
        await byClient.CreateUserWithPhoneAsync("+12025550115");
        await byClient.CreateUserWithPhoneAsync("+12025550116");
        await byClient.Service.StartForClientAsync(new SqlOSPhoneOtpStartRequest("+12025550115", "test-client", null));
        var clientAct = async () => await byClient.Service.StartForClientAsync(new SqlOSPhoneOtpStartRequest("+12025550116", "test-client", null));
        await clientAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Too many sign-in code requests. Try again later.");
    }

    [TestMethod]
    public async Task PhoneOtp_Start_RejectsBlockedCountryOrInvalidPhone()
    {
        using var blocked = await PhoneOtpHarness.CreateAsync(options =>
        {
            options.PhoneOtp.CountryDenyList = ["US"];
        });

        var blockedAct = async () => await blocked.Service.StartForClientAsync(
            new SqlOSPhoneOtpStartRequest("+12025550121", "test-client", null));
        await blockedAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Phone number country is not allowed.");

        using var invalid = await PhoneOtpHarness.CreateAsync();
        var invalidAct = async () => await invalid.Service.StartForClientAsync(
            new SqlOSPhoneOtpStartRequest("not-a-phone", "test-client", null));
        await invalidAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Phone number is invalid.");
    }

    [TestMethod]
    public async Task PhoneOtp_Verify_ValidCode_AuthenticatesWhenPolicyAllows()
    {
        using var harness = await PhoneOtpHarness.CreateAsync(options =>
        {
            options.PhoneOtp.SatisfiesMfa = true;
        });
        var user = await harness.CreateUserWithPhoneAsync("+12025550130");
        var start = await harness.Service.StartForClientAsync(new SqlOSPhoneOtpStartRequest("+12025550130", "test-client", null));

        var result = await harness.Service.VerifyAsync(new SqlOSPhoneOtpVerifyRequest(start.ChallengeToken, "123456"));

        result.User.Id.Should().Be(user.Id);
        result.AuthenticationMethod.Should().Be("phone_otp");
        harness.Channel.CheckRequests.Should().ContainSingle()
            .Which.Context.ProviderChallengeId.Should().Be("ve-1");
        new SqlOSMfaPolicyService(harness.Options).SatisfiesStrongMfa(result.AuthenticationMethod).Should().BeTrue();
    }

    [TestMethod]
    public async Task PhoneOtp_Verify_InvalidCode_IsRejected()
    {
        using var harness = await PhoneOtpHarness.CreateAsync();
        await harness.CreateUserWithPhoneAsync("+12025550131");
        var start = await harness.Service.StartForClientAsync(new SqlOSPhoneOtpStartRequest("+12025550131", "test-client", null));

        var act = async () => await harness.Service.VerifyAsync(new SqlOSPhoneOtpVerifyRequest(start.ChallengeToken, "000000"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The sign-in code is invalid or expired.");
        harness.Channel.CheckRequests.Should().ContainSingle(x => x.Code == "000000");
    }

    [TestMethod]
    public async Task PhoneOtp_Verify_CannotCrossSatisfyAnotherProviderChallenge()
    {
        using var harness = await PhoneOtpHarness.CreateAsync();
        await harness.CreateUserWithPhoneAsync("+12025550137");
        var start = await harness.Service.StartForClientAsync(new SqlOSPhoneOtpStartRequest("+12025550137", "test-client", null));
        var challenge = await harness.Context.Set<SqlOSPhoneOtpChallenge>()
            .SingleAsync(x => x.ChallengeTokenHash == harness.Crypto.HashToken(start.ChallengeToken));
        challenge.ProviderChallengeId = "ve-from-admin-test";
        await harness.Context.SaveChangesAsync();

        await FluentActions.Invoking(() => harness.Service.VerifyAsync(new SqlOSPhoneOtpVerifyRequest(start.ChallengeToken, "123456")))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The sign-in code is invalid or expired.");
        harness.Channel.CheckRequests.Should().ContainSingle()
            .Which.Context.ProviderChallengeId.Should().Be("ve-from-admin-test");
    }

    [TestMethod]
    public async Task PhoneOtp_Verify_ExpiredOrReplayedCode_IsRejected()
    {
        using var expired = await PhoneOtpHarness.CreateAsync();
        await expired.CreateUserWithPhoneAsync("+12025550132");
        var expiredStart = await expired.Service.StartForClientAsync(new SqlOSPhoneOtpStartRequest("+12025550132", "test-client", null));
        var expiredChallengeHash = expired.Crypto.HashToken(expiredStart.ChallengeToken);
        var expiredChallenge = await expired.Context.Set<SqlOSPhoneOtpChallenge>()
            .SingleAsync(x => x.ChallengeTokenHash == expiredChallengeHash);
        expiredChallenge.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await expired.Context.SaveChangesAsync();

        var expiredAct = async () => await expired.Service.VerifyAsync(new SqlOSPhoneOtpVerifyRequest(expiredStart.ChallengeToken, "123456"));
        await expiredAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The sign-in code is invalid or expired.");
        expired.Channel.CheckRequests.Should().BeEmpty();

        using var replayed = await PhoneOtpHarness.CreateAsync();
        await replayed.CreateUserWithPhoneAsync("+12025550133");
        var replayStart = await replayed.Service.StartForClientAsync(new SqlOSPhoneOtpStartRequest("+12025550133", "test-client", null));
        await replayed.Service.VerifyAsync(new SqlOSPhoneOtpVerifyRequest(replayStart.ChallengeToken, "123456"));

        var replayAct = async () => await replayed.Service.VerifyAsync(new SqlOSPhoneOtpVerifyRequest(replayStart.ChallengeToken, "123456"));
        await replayAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The sign-in code is invalid or expired.");
    }

    [TestMethod]
    public void MfaPolicy_SmsOtp_DoesNotSatisfyStrongMfaByDefault()
    {
        var policy = new SqlOSMfaPolicyService(Options.Create(new SqlOSAuthServerOptions()));

        policy.SatisfiesStrongMfa("phone_otp").Should().BeFalse();
        policy.SatisfiesAdminOwnerStrongMfa("phone_otp").Should().BeFalse();
    }

    [TestMethod]
    public void MfaPolicy_SmsOtp_SatisfiesMfaOnlyWhenExplicitlyAllowed()
    {
        var options = new SqlOSAuthServerOptions();
        options.PhoneOtp.SatisfiesMfa = true;
        var policy = new SqlOSMfaPolicyService(Options.Create(options));

        policy.SatisfiesStrongMfa("phone_otp").Should().BeTrue();
        policy.SatisfiesAdminOwnerStrongMfa("phone_otp").Should().BeFalse();
    }

    [TestMethod]
    public void PhoneOtp_Disabled_ByDefault_And_StartupValidationFailsWhenMisconfigured()
    {
        new SqlOSAuthServerOptions().PhoneOtp.IsConfigured.Should().BeFalse();

        var services = new ServiceCollection();
        Action act = () => services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.PhoneOtp.Enabled = true;
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AuthServer.PhoneOtp requires TwilioAccountSid, TwilioAuthToken, and TwilioVerifyServiceSid*");
    }

    [TestMethod]
    public async Task PhoneChange_RequiresAuthenticatedSession()
    {
        using var harness = await PhoneOtpHarness.CreateAsync();

        var startAct = async () => await harness.Service.StartEnrollmentAsync(null, "+12025550140");
        await startAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Sign in before changing phone numbers.");

        var verifyAct = async () => await harness.Service.VerifyEnrollmentAsync(
            null,
            new SqlOSPhoneOtpEnrollmentVerifyRequest("challenge", "123456"));
        await verifyAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Sign in before changing phone numbers.");
    }

    [TestMethod]
    public async Task PhoneOtp_Audit_WritesSendVerifyFailureAndThrottleEvents()
    {
        var rejectChannel = new FakeOtpDeliveryChannel { RejectStarts = true };
        using var sendFailed = await PhoneOtpHarness.CreateAsync(
            options => options.PhoneOtp.ResendCooldown = TimeSpan.Zero,
            rejectChannel);
        await sendFailed.CreateUserWithPhoneAsync("+12025550150");
        var sendAct = async () => await sendFailed.Service.StartForClientAsync(
            new SqlOSPhoneOtpStartRequest("+12025550150", "test-client", null),
            CreateHttpContext("203.0.113.50"));
        await sendAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("We couldn't send a sign-in code right now.");

        using var verifyFailed = await PhoneOtpHarness.CreateAsync();
        await verifyFailed.CreateUserWithPhoneAsync("+12025550151");
        var start = await verifyFailed.Service.StartForClientAsync(
            new SqlOSPhoneOtpStartRequest("+12025550151", "test-client", null),
            CreateHttpContext("203.0.113.51"));
        var verifyAct = async () => await verifyFailed.Service.VerifyAsync(new SqlOSPhoneOtpVerifyRequest(start.ChallengeToken, "999999"));
        await verifyAct.Should().ThrowAsync<InvalidOperationException>();

        using var throttled = await PhoneOtpHarness.CreateAsync(options =>
        {
            options.PhoneOtp.ResendCooldown = TimeSpan.Zero;
            options.PhoneOtp.MaxSendsPerPhone = 1;
        });
        await throttled.CreateUserWithPhoneAsync("+12025550152");
        await throttled.Service.StartForClientAsync(
            new SqlOSPhoneOtpStartRequest("+12025550152", "test-client", null),
            CreateHttpContext("203.0.113.52"));
        var throttleAct = async () => await throttled.Service.StartForClientAsync(
            new SqlOSPhoneOtpStartRequest("+12025550152", "test-client", null),
            CreateHttpContext("203.0.113.52"));
        await throttleAct.Should().ThrowAsync<InvalidOperationException>();

        var sendAuditTypes = await sendFailed.ListAuditTypesAsync();
        var verifyAuditTypes = await verifyFailed.ListAuditTypesAsync();
        var throttleAuditTypes = await throttled.ListAuditTypesAsync();
        sendAuditTypes.Should().Contain("phone_otp.send_failed");
        verifyAuditTypes.Should().Contain("phone_otp.verify_failed");
        throttleAuditTypes.Should().Contain("phone_otp.rate_limit_rejected");
    }

    private static DefaultHttpContext CreateHttpContext(string? ipAddress = null)
    {
        var context = new DefaultHttpContext();
        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);
        }

        context.Request.Headers.UserAgent = "SqlOSPhoneOtpTests";
        return context;
    }

    private sealed class PhoneOtpHarness : IDisposable
    {
        public required TestSqlOSInMemoryDbContext Context { get; init; }
        public required SqlOSAdminService Admin { get; init; }
        public required SqlOSCryptoService Crypto { get; init; }
        public required SqlOSPhoneOtpService Service { get; init; }
        public required IOptions<SqlOSAuthServerOptions> Options { get; init; }
        public required FakeOtpDeliveryChannel Channel { get; init; }

        public static async Task<PhoneOtpHarness> CreateAsync(
            Action<SqlOSAuthServerOptions>? configure = null,
            FakeOtpDeliveryChannel? channel = null)
        {
            var context = new TestSqlOSInMemoryDbContext(
                new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options);

            var authOptions = new SqlOSAuthServerOptions();
            authOptions.EnableLocalPasswordAuth = false;
            authOptions.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
            authOptions.SeedAuthPage(page =>
            {
                page.EnabledCredentialTypes = ["phone_otp"];
                page.EnablePasswordSignup = false;
            });
            authOptions.ConfigurePhoneOtp(phone =>
            {
                phone.Enabled = true;
                phone.TwilioAccountSid = "AC00000000000000000000000000000000";
                phone.TwilioAuthToken = "test-token";
                phone.TwilioVerifyServiceSid = "VA00000000000000000000000000000000";
                phone.ResendCooldown = TimeSpan.Zero;
            });
            configure?.Invoke(authOptions);

            var options = Microsoft.Extensions.Options.Options.Create(authOptions);
            var emailSender = new TestAuthEmailSender();
            var crypto = TestCryptoService.Create(context, options, new EphemeralDataProtectionProvider());
            var admin = new SqlOSAdminService(context, options, crypto);
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var fakeChannel = channel ?? new FakeOtpDeliveryChannel();
            var phoneOtp = new SqlOSPhoneOtpService(context, admin, crypto, settings, fakeChannel, options);

            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.UpsertSeededAuthPageSettingsAsync();

            return new PhoneOtpHarness
            {
                Context = context,
                Admin = admin,
                Crypto = crypto,
                Service = phoneOtp,
                Options = options,
                Channel = fakeChannel
            };
        }

        public async Task<SqlOSUser> CreateUserWithPhoneAsync(string phoneNumber)
        {
            var user = await Admin.CreateUserAsync(new SqlOSCreateUserRequest(
                $"Phone User {Guid.NewGuid():N}",
                $"phone-{Guid.NewGuid():N}@example.com",
                "P@ssword123!"));
            await Service.AddVerifiedPhoneNumberAsync(user, phoneNumber);
            return user;
        }

        public async Task<string[]> ListAuditTypesAsync()
            => await Context.Set<SqlOSAuditEvent>()
                .OrderBy(x => x.OccurredAt)
                .Select(x => x.EventType)
                .ToArrayAsync();

        public void Dispose()
            => Context.Dispose();
    }

    private sealed class FakeOtpDeliveryChannel : ISqlOSOtpDeliveryChannel
    {
        public List<(string PhoneNumber, SqlOSOtpDeliveryContext Context)> StartRequests { get; } = [];
        public List<(string PhoneNumber, string Code, SqlOSOtpDeliveryContext Context)> CheckRequests { get; } = [];
        public string ApprovedCode { get; set; } = "123456";
        public bool RejectStarts { get; set; }

        public Task<SqlOSOtpDeliveryStartResult> StartAsync(
            string e164PhoneNumber,
            SqlOSOtpDeliveryContext context,
            CancellationToken cancellationToken = default)
        {
            StartRequests.Add((e164PhoneNumber, context));
            return Task.FromResult(RejectStarts
                ? new SqlOSOtpDeliveryStartResult(false, "test_verify", null, "failed", "provider_unavailable")
                : new SqlOSOtpDeliveryStartResult(true, "test_verify", $"ve-{StartRequests.Count}", "pending"));
        }

        public Task<SqlOSOtpDeliveryCheckResult> CheckAsync(
            string e164PhoneNumber,
            string code,
            SqlOSOtpDeliveryContext context,
            CancellationToken cancellationToken = default)
        {
            CheckRequests.Add((e164PhoneNumber, code, context));
            var expectedProviderChallengeId = $"ve-{StartRequests.Count}";
            var approved = string.Equals(code, ApprovedCode, StringComparison.Ordinal)
                && string.Equals(context.ProviderChallengeId, expectedProviderChallengeId, StringComparison.Ordinal);
            return Task.FromResult(new SqlOSOtpDeliveryCheckResult(
                approved,
                "test_verify",
                $"ve-{CheckRequests.Count}",
                approved ? "approved" : "denied",
                approved ? null : "bad_code"));
        }
    }
}
