using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Public;

/// <summary>
/// #424: in 7.2.1 the email-code attempt limit and the email-code and sign-in-link send limits are
/// checked with a read, then a write, and nothing makes the pair atomic, so requests sent together
/// all pass the read before any of them writes. Each scenario sends six requests in parallel and
/// uses a <see cref="SqlCommandBarrier"/> to hold every request's write until all six have done
/// their reads, which is the interleaving an attacker gets by sending them together; without it
/// the outcome would depend on timing. A limit that is atomic meets the barrier at its own
/// reservation, and the transcript shows that it holds. The parallel step's audit events are
/// recorded in content order, because parallel requests write them in whatever order their
/// threads run (<see cref="AuditOrder.Content"/>).
/// </summary>
[TestClass]
public sealed class PublicRaceScenarios
{
    private const string Client = BehaviorLockConstants.AppClientId;
    private const int Parallel = 6;

    [Scenario]
    [Covers("POST /sqlos/auth/email-otp/start")]
    [Covers("POST /sqlos/auth/email-otp/verify")]
    public async Task Parallel_wrong_codes_spend_every_attempt_so_the_right_code_no_longer_signs_in()
    {
        // #424, fixed: each guess spends its attempt with one conditional update before its code is
        // compared, so the six guesses spend the five attempts one by one, the guess that spent the
        // last one closes the challenge, and the sixth finds no attempt left. 7.2.1 let every guess
        // write an attempt count of one, and the right code still signed in.
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
            (guess, _) => $"parallel wrong guess {guess}",
            "five guesses spent the five attempts, the last closing the challenge (max_attempts); the sixth found none left");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/verify", new { challengeToken, code }),
            "the six wrong guesses closed the challenge: the right code is refused");

        await t.ObserveAuditAsync("nothing more: a closed challenge spends no attempt");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/email-otp/start")]
    public async Task Parallel_code_requests_never_exceed_the_hourly_limit()
    {
        // #424, fixed: each request reserves the address's, the IP address's and the client's
        // buckets in one atomic step before it writes, so five of the six are admitted and the
        // sixth is refused. 7.2.1 counted the recent challenges, then inserted, and admitted all six.
        var barrier = new SqlCommandBarrier(Parallel, "INSERT", "SqlOSEmailOtpChallenges");
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted, options => options.ConfigureServices = PublicHost.Interleave(barrier));
        var alice = await t.Setup.CreateUserAsync("alice");

        await SendInParallelAsync(
            t,
            barrier,
            _ => t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = alice.Email, clientId = Client }),
            (request, exchange) => exchange.StatusCode == 200
                ? $"parallel request {request}: admitted, and a code is sent"
                : $"parallel request {request}: refused, the address's hourly limit is reached",
            "five challenges started and five emails queued; one request refused");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = alice.Email, clientId = Client }),
            "a seventh request is refused as well");

        await t.ObserveAuditAsync("the rejection");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/magic-link/start")]
    public async Task Parallel_link_requests_never_exceed_the_hourly_limit()
    {
        // #424, fixed: each request reserves the address's, the IP address's and the client's
        // buckets in one atomic step before it writes, so five of the six are admitted and the
        // sixth is refused. 7.2.1 counted the recent links, then inserted, and admitted all six.
        var barrier = new SqlCommandBarrier(Parallel, "INSERT", "SqlOSTemporaryTokens");
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted, options => options.ConfigureServices = PublicHost.Interleave(barrier));
        var alice = await t.Setup.CreateUserAsync("alice");

        await SendInParallelAsync(
            t,
            barrier,
            _ => t.Api.PostJsonAsync("/sqlos/auth/magic-link/start", new { email = alice.Email, clientId = Client }),
            (request, exchange) => exchange.StatusCode == 200
                ? $"parallel request {request}: admitted, and a link is sent"
                : $"parallel request {request}: refused, the address's hourly limit is reached",
            "five links requested and five emails queued; one request refused");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/magic-link/start", new { email = alice.Email, clientId = Client }),
            "a seventh request is refused as well");

        await t.ObserveAuditAsync("the rejection");
        await t.ApproveAsync();
    }

    /// <summary>
    /// Sends <see cref="Parallel"/> requests together through the armed barrier, observes them in
    /// the order they were listed, the answered ones before the refused ones (a stable sort by
    /// status: when a limit admits only some of the requests, which ones it refuses depends on
    /// thread timing), and records the audit events they wrote in content order: the requests write
    /// them in whatever order their threads run.
    /// </summary>
    private static async Task SendInParallelAsync(
        Transcript t,
        SqlCommandBarrier barrier,
        Func<int, Task<HttpExchange>> request,
        Func<string, HttpExchange, string> caption,
        string auditCaption)
    {
        barrier.Arm();
        var exchanges = await Task.WhenAll(Enumerable.Range(1, Parallel).Select(async index =>
        {
            try
            {
                return await request(index);
            }
            finally
            {
                barrier.Depart();
            }
        }));
        barrier.Disarm();
        var observed = exchanges.OrderBy(exchange => exchange.StatusCode).ToList();
        for (var index = 0; index < observed.Count; index++)
        {
            t.Observe(
                observed[index],
                caption($"{(index + 1).ToString(CultureInfo.InvariantCulture)} of {Parallel.ToString(CultureInfo.InvariantCulture)}", observed[index]));
        }

        await t.ObserveAuditAsync(auditCaption, AuditOrder.Content);
    }
}
