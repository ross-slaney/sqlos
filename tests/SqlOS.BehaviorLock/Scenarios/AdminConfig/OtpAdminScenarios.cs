using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Interfaces;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminConfig;

/// <summary>
/// OTP delivery readiness and test deliveries (<c>/sqlos/admin/auth/api/otp/*</c>): the readiness
/// report and its recent diagnostics, email and SMS test sends, input validation, the per-destination
/// limit, a provider rejection, and a method the host did not configure.
/// </summary>
[TestClass]
public sealed class OtpAdminScenarios
{
    private const string ReadinessRoute = "/sqlos/admin/auth/api/otp/readiness";
    private const string TestDeliveryRoute = "/sqlos/admin/auth/api/otp/test-delivery";

    /// <summary>
    /// The hosted profile sends email codes through the capturing sender but configures no email
    /// OTP delivery, so readiness reports email as not locally configured. These scenarios configure
    /// Azure Communication Services delivery (the validator requires the connection string and the
    /// sender address together); the host still replaces the sender with the capturing fake.
    /// </summary>
    private static void ConfigureEmailOtpSender(ScenarioOptions options)
        => options.ConfigureSqlOS = sqlos => sqlos.AuthServer.ConfigureEmailOtp(email =>
        {
            email.AzureCommunicationServicesConnectionString = "endpoint=https://behavior-lock.communication.azure.com/;accesskey=YmVoYXZpb3ItbG9jaw==";
            email.FromAddress = "codes@sqlos.example.test";
        });

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/otp/readiness")]
    [Covers("POST /sqlos/admin/auth/api/otp/test-delivery")]
    public async Task Operator_checks_otp_readiness_and_sends_test_deliveries()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Hosted, ConfigureEmailOtpSender);

        t.Observe(
            await t.Operator.GetAsync(ReadinessRoute, options => options.WithoutCredentials()),
            "without operator credentials the readiness report is not found");
        t.Observe(await t.Operator.GetAsync(ReadinessRoute), "email and SMS codes are both ready");
        t.Observe(
            await t.Operator.PostJsonAsync(TestDeliveryRoute, new { method = " Email ", destination = " Ops.Team@Example.Test " }),
            "send a test email: the destination is normalized and masked");
        t.Observe(
            await t.Operator.PostJsonAsync(TestDeliveryRoute, new { method = "phone", destination = "+1 (202) 555-0149" }),
            "send a test SMS through the provider");
        t.Observe(await t.Operator.GetAsync(ReadinessRoute), "the readiness report lists both deliveries as recent diagnostics");

        await t.ObserveAuditAsync("each test delivery is audited with a masked destination");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/otp/test-delivery")]
    public async Task Otp_test_delivery_rejects_bad_input_and_limits_each_destination()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Hosted, ConfigureEmailOtpSender);

        t.Observe(await t.Operator.PostJsonAsync(TestDeliveryRoute, new { method = "fax", destination = "+12025550149" }), "an unknown method");
        t.Observe(await t.Operator.PostJsonAsync(TestDeliveryRoute, new { method = "email", destination = "not-an-address" }), "an invalid email address");
        t.Observe(await t.Operator.PostJsonAsync(TestDeliveryRoute, new { method = "phone", destination = "12" }), "an invalid phone number");
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            t.Observe(
                await t.Operator.PostJsonAsync(TestDeliveryRoute, new { method = "email", destination = "limit@example.test" }),
                $"test email {attempt} of 3 to one destination");
        }

        t.Observe(
            await t.Operator.PostJsonAsync(TestDeliveryRoute, new { method = "email", destination = "LIMIT@example.test" }),
            "a fourth test to the same (normalized) destination within the hour is refused");

        await t.ObserveAuditAsync("successful and refused test deliveries");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/otp/test-delivery")]
    [Covers("GET /sqlos/admin/auth/api/otp/readiness")]
    public async Task A_provider_rejection_fails_the_test_delivery_and_shows_in_diagnostics()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Hosted, options =>
            options.ConfigureServices = services =>
            {
                services.RemoveAll<ISqlOSOtpDeliveryChannel>();
                services.AddSingleton<ISqlOSOtpDeliveryChannel, RejectingOtpDeliveryChannel>();
            });

        t.Observe(
            await t.Operator.PostJsonAsync(TestDeliveryRoute, new { method = "phone", destination = "+12025550149" }),
            "the SMS provider rejects the send; the operator sees a generic failure");
        t.Observe(await t.Operator.GetAsync(ReadinessRoute), "the failure appears in the readiness diagnostics");

        await t.ObserveAuditAsync("the failed test delivery is audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/otp/readiness")]
    [Covers("POST /sqlos/admin/auth/api/otp/test-delivery")]
    public async Task Otp_test_delivery_is_unavailable_for_a_method_the_host_did_not_configure()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback);

        t.Observe(await t.Operator.GetAsync(ReadinessRoute), "the readiness report explains what is missing for each method");
        t.Observe(
            await t.Operator.PostJsonAsync(TestDeliveryRoute, new { method = "phone", destination = "+12025550149" }),
            "an SMS test is unavailable: SMS codes are not configured");
        t.Observe(
            await t.Operator.PostJsonAsync(TestDeliveryRoute, new { method = "email", destination = "ops@example.test" }),
            "an email test is unavailable: no sender address is configured");

        await t.ObserveAuditAsync("unavailable methods are not audited");
        await t.ApproveAsync();
    }

    /// <summary>An SMS provider that refuses every send, as a misconfigured provider account does.</summary>
    private sealed class RejectingOtpDeliveryChannel : ISqlOSOtpDeliveryChannel
    {
        public Task<SqlOSOtpDeliveryStartResult> StartAsync(
            string e164PhoneNumber,
            SqlOSOtpDeliveryContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SqlOSOtpDeliveryStartResult(false, "twilio_verify", null, "failed", "Provider rejected the destination."));

        public Task<SqlOSOtpDeliveryCheckResult> CheckAsync(
            string e164PhoneNumber,
            string code,
            SqlOSOtpDeliveryContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SqlOSOtpDeliveryCheckResult(false, "twilio_verify", context.ProviderChallengeId, "failed"));
    }
}
