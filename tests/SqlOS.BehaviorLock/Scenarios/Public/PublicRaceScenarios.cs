using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Public;

/// <summary>
/// #424: the email-code attempt limit and the email-code and sign-in-link send limits are checked
/// with a read, then a write, and nothing makes the pair atomic. Requests sent together all pass
/// the read before any of them writes. Each scenario sends six requests in parallel and uses a
/// <see cref="SqlCommandBarrier"/> to hold every request's write until all six have done their
/// reads, which is the interleaving an attacker gets by sending them together; without it the
/// outcome would depend on timing. The parallel step's audit events are recorded in content order,
/// because parallel requests write them in whatever order their threads run. When the limits
/// become atomic, these approvals change.
/// </summary>
[TestClass]
public sealed class PublicRaceScenarios
{
    private const string Client = BehaviorLockConstants.AppClientId;
    private const int Parallel = 6;

    [Scenario]
    [Covers("POST /sqlos/auth/email-otp/start")]
    [Covers("POST /sqlos/auth/email-otp/verify")]
    public async Task Parallel_wrong_codes_count_as_one_attempt_so_the_right_code_still_signs_in_CurrentBehavior_KnownDefect_424()
    {
        var barrier = new SqlCommandBarrier(Parallel, "UPDATE", "SqlOSEmailOtpChallenges");
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted, options => options.ConfigureServices = PublicHost.Interleave(barrier));
        var alice = await t.Setup.CreateUserAsync("alice");

        var started = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = alice.Email, clientId = Client }),
            "an attacker who knows Alice's address starts a challenge (the code goes to Alice)");
        var challengeToken = started.JsonString("challengeToken");
        var code = t.LatestEmailCode(alice.Email);
        await t.ObserveAuditAsync("the challenge");

        await SendInParallelAsync(
            t,
            barrier,
            guess => t.Api.PostJsonAsync("/sqlos/auth/email-otp/verify", new { challengeToken, code = PublicSetup.WrongCode(code, guess) }),
            guess => $"parallel wrong guess {guess}",
            "every guess saw an attempt count of zero, so each wrote one");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/verify", new { challengeToken, code }),
            "six wrong guesses exceeded the limit of five, yet the challenge is still open: the right code signs in");

        await t.ObserveAuditAsync("the successful sign-in");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/email-otp/start")]
    public async Task Parallel_code_requests_exceed_the_hourly_limit_CurrentBehavior_KnownDefect_424()
    {
        var barrier = new SqlCommandBarrier(Parallel, "INSERT", "SqlOSEmailOtpChallenges");
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted, options => options.ConfigureServices = PublicHost.Interleave(barrier));
        var alice = await t.Setup.CreateUserAsync("alice");

        await SendInParallelAsync(
            t,
            barrier,
            _ => t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = alice.Email, clientId = Client }),
            request => $"parallel request {request}: admitted, and a code is sent",
            "six challenges started and six emails queued");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = alice.Email, clientId = Client }),
            "six codes went out against a limit of five an hour; only now is the limit enforced");

        await t.ObserveAuditAsync("the rejection");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/magic-link/start")]
    public async Task Parallel_link_requests_exceed_the_hourly_limit_CurrentBehavior_KnownDefect_424()
    {
        var barrier = new SqlCommandBarrier(Parallel, "INSERT", "SqlOSTemporaryTokens");
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted, options => options.ConfigureServices = PublicHost.Interleave(barrier));
        var alice = await t.Setup.CreateUserAsync("alice");

        await SendInParallelAsync(
            t,
            barrier,
            _ => t.Api.PostJsonAsync("/sqlos/auth/magic-link/start", new { email = alice.Email, clientId = Client }),
            request => $"parallel request {request}: admitted, and a link is sent",
            "six links requested and six emails queued");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/magic-link/start", new { email = alice.Email, clientId = Client }),
            "six links went out against a limit of five an hour; only now is the limit enforced");

        await t.ObserveAuditAsync("the rejection");
        await t.ApproveAsync();
    }

    /// <summary>Sends <see cref="Parallel"/> requests together through the armed barrier and observes them in order.</summary>
    private static Task SendInParallelAsync(
        Transcript t,
        SqlCommandBarrier barrier,
        Func<int, Task<HttpExchange>> request,
        Func<string, string> caption,
        string auditCaption)
        => t.ObserveAuditInContentOrderAsync(
            async () =>
            {
                barrier.Arm();
                var exchanges = await ParallelRequests.SendAsync(Enumerable.Range(1, Parallel)
                    .Select(number => (Func<Task<HttpExchange>>)(() => request(number)))
                    .ToList());
                barrier.Disarm();
                for (var index = 0; index < exchanges.Count; index++)
                {
                    t.Observe(exchanges[index], caption($"{(index + 1).ToString(CultureInfo.InvariantCulture)} of {Parallel.ToString(CultureInfo.InvariantCulture)}"));
                }
            },
            auditCaption);
}
