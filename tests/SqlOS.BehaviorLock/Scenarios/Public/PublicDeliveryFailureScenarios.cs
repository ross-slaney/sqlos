using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Fakes;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using SqlOS.Email.Interfaces;

namespace SqlOS.BehaviorLock.Scenarios.Public;

/// <summary>
/// What the public account API answers when the transactional email provider cannot deliver. The
/// host's sender reports itself unconfigured, so every send is recorded as failed. Sign-in codes
/// and links fail visibly and invalidate what they minted; password reset and email verification
/// keep their generic answers (they must not reveal whether an account exists) and audit the failure.
/// </summary>
[TestClass]
public sealed class PublicDeliveryFailureScenarios
{
    private const string Client = BehaviorLockConstants.AppClientId;

    [Scenario]
    [Covers("POST /sqlos/auth/email-otp/start")]
    [Covers("POST /sqlos/auth/magic-link/start")]
    [Covers("POST /sqlos/auth/password/forgot")]
    [Covers("POST /sqlos/auth/email/verification-email")]
    public async Task Undeliverable_email_fails_codes_and_links_but_keeps_reset_and_verification_generic()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted, options => options.ConfigureServices = services =>
        {
            services.RemoveAll<ISqlOSEmailSender>();
            services.AddSingleton<ISqlOSEmailSender>(provider => new CapturingEmailSender(provider.GetRequiredService<EffectLog>(), isConfigured: false));
        });
        var alice = await t.Setup.CreateUserAsync("alice");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = alice.Email, clientId = Client }),
            "an email code that cannot be delivered escapes the endpoint");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/magic-link/start", new { email = alice.Email, clientId = Client }),
            "so does a sign-in link");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/forgot", new { email = alice.Email, clientId = Client }),
            "a password reset keeps its generic answer");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email/verification-email", new { email = alice.Email }),
            "and so does email verification");

        await t.ObserveAuditAsync("each failed delivery and its effect on the minted code, link, or token");
        await t.ApproveAsync();
    }
}
