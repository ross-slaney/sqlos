using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Email.Interfaces;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// Sign-up verification codes are checked before the sign-up transaction opens, so a wrong code
/// stays counted when that transaction rolls back (#449). Every surface that verifies a sign-up
/// code is driven here against SQL: the hosted forms, the headless API, and SqlOSAuthService.
/// </summary>
[TestClass]
public sealed class SignupCodeAttemptIntegrationTests
{
    private const int MaxEmailAttempts = 5;
    private const string InvalidCodeMessage = "The sign-in code is invalid or expired.";

    [TestMethod]
    public async Task HostedEmailSignup_FiveWrongCodesInvalidateTheChallenge_AndTheRightCodeCreatesNoAccount()
    {
        await using var host = await SignupCodeHost.CreateAsync();
        var email = UniqueEmail("hosted-email");
        var page = await host.StartHostedEmailSignupAsync(email);
        var code = host.LatestEmailCode(email);

        for (var attempt = 1; attempt <= MaxEmailAttempts; attempt++)
        {
            using var wrong = await host.PostHostedFormAsync(page, "/sqlos/auth/signup/email-otp/verify", page.VerifyFields(WrongCode(code)));
            wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest, "wrong code #{0} is rejected", attempt);
        }

        using var right = await host.PostHostedFormAsync(page, "/sqlos/auth/signup/email-otp/verify", page.VerifyFields(code));
        right.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the challenge was exhausted by the wrong codes");
        (await right.Content.ReadAsStringAsync()).Should().Contain(InvalidCodeMessage);

        await host.AssertEmailChallengeExhaustedAsync(email);
        await host.AssertNoAccountAsync(email);
    }

    [TestMethod]
    public async Task HostedEmailSignup_ConcurrentWrongCodes_AreComparedAtMostMaxAttemptsTimes()
    {
        const int guesses = 10;
        var barrier = new FirstChallengeUpdateBarrier("SqlOSEmailOtpChallenges", guesses);
        await using var host = await SignupCodeHost.CreateAsync(barrier: barrier);
        var email = UniqueEmail("hosted-race");
        var page = await host.StartHostedEmailSignupAsync(email);
        var code = host.LatestEmailCode(email);

        // Every guess loads the challenge before any of them writes its attempt.
        barrier.Arm();
        var responses = await Task.WhenAll(Enumerable.Range(0, guesses).Select(_ =>
            host.PostHostedFormAsync(page, "/sqlos/auth/signup/email-otp/verify", page.VerifyFields(WrongCode(code)))));
        barrier.Disarm();

        barrier.Held.Should().Be(guesses, "every concurrent guess must reach its attempt write together");
        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.BadRequest);
        foreach (var response in responses)
        {
            response.Dispose();
        }

        using var right = await host.PostHostedFormAsync(page, "/sqlos/auth/signup/email-otp/verify", page.VerifyFields(code));
        right.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await host.AssertEmailChallengeExhaustedAsync(email);
        await host.AssertNoAccountAsync(email);
    }

    [TestMethod]
    public async Task HostedEmailSignup_RightCodeWithinTheLimit_CreatesTheAccountOnce()
    {
        await using var host = await SignupCodeHost.CreateAsync();
        var email = UniqueEmail("hosted-within");
        var page = await host.StartHostedEmailSignupAsync(email);
        var code = host.LatestEmailCode(email);

        for (var attempt = 1; attempt < MaxEmailAttempts; attempt++)
        {
            using var wrong = await host.PostHostedFormAsync(page, "/sqlos/auth/signup/email-otp/verify", page.VerifyFields(WrongCode(code)));
            wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        using var right = await host.PostHostedFormAsync(page, "/sqlos/auth/signup/email-otp/verify", page.VerifyFields(code));
        var redirect = await HostedAuthorizeTokenFixture.ReadClientRedirectAsync(right);
        var authorizationCode = QueryHelpers.ParseQuery(redirect.Query)["code"].ToString();
        authorizationCode.Should().NotBeNullOrWhiteSpace("the fifth attempt is still within the limit");
        using var tokens = await host.Fixture.ExchangeAuthorizationCodeAsync(authorizationCode, page.CodeVerifier);
        tokens.RootElement.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();

        using var replay = await host.PostHostedFormAsync(page, "/sqlos/auth/signup/email-otp/verify", page.VerifyFields(code));
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the spent code cannot create a second account");

        await using var db = host.CreateDbContext();
        var challenge = await db.Set<SqlOSEmailOtpChallenge>().AsNoTracking().SingleAsync(x => x.Email == email);
        challenge.ConsumedAt.Should().NotBeNull();
        challenge.AttemptCount.Should().Be(MaxEmailAttempts);
        var userId = await db.Set<SqlOSUserEmail>().Where(x => x.Email == email).Select(x => x.UserId).SingleAsync();
        (await db.Set<SqlOSSession>().CountAsync(x => x.UserId == userId)).Should().Be(1);
        (await db.Set<SqlOSTemporaryToken>().SingleAsync(x => x.Purpose == "email_otp_signup")).ConsumedAt.Should().NotBeNull();
        (await host.CountAuditAsync("email_otp.verify_failed")).Should().Be(MaxEmailAttempts - 1);
        (await host.CountAuditAsync("email_otp.verify_succeeded")).Should().Be(1);
    }

    [TestMethod]
    public async Task HostedEmailSignup_ConcurrentRightCodes_CreateExactlyOneAccount()
    {
        const int submissions = 3;
        var barrier = new FirstChallengeUpdateBarrier("SqlOSEmailOtpChallenges", submissions);
        await using var host = await SignupCodeHost.CreateAsync(barrier: barrier);
        var email = UniqueEmail("hosted-once");
        var page = await host.StartHostedEmailSignupAsync(email);
        var code = host.LatestEmailCode(email);

        // All three pass the code check before any of them creates the account.
        barrier.Arm();
        var responses = await Task.WhenAll(Enumerable.Range(0, submissions).Select(_ =>
            host.PostHostedFormAsync(page, "/sqlos/auth/signup/email-otp/verify", page.VerifyFields(code))));
        barrier.Disarm();

        barrier.Held.Should().Be(submissions);
        var redirects = 0;
        foreach (var response in responses)
        {
            var body = await response.Content.ReadAsStringAsync();
            if (HostedAuthorizeTokenFixture.TryReadClientRedirect(response, body) is { } location
                && QueryHelpers.ParseQuery(location.Query).ContainsKey("code"))
            {
                redirects++;
            }
            else
            {
                response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            }

            response.Dispose();
        }

        redirects.Should().Be(1);
        await using var db = host.CreateDbContext();
        var userIds = await db.Set<SqlOSUserEmail>().Where(x => x.Email == email).Select(x => x.UserId).ToListAsync();
        userIds.Should().ContainSingle();
        (await db.Set<SqlOSAuthorizationCode>().CountAsync(x => x.UserId == userIds[0])).Should().Be(1);
        (await db.Set<SqlOSEmailOtpChallenge>().AsNoTracking().SingleAsync(x => x.Email == email)).ConsumedAt.Should().NotBeNull();
    }

    [TestMethod]
    public async Task HostedPhoneSignup_ARejectedCodeInvalidatesTheChallenge_AndTheRightCodeCreatesNoAccount()
    {
        await using var host = await SignupCodeHost.CreateAsync();
        const string phoneNumber = "+12025550181";
        var page = await host.StartHostedPhoneSignupAsync(phoneNumber);

        using var wrong = await host.PostHostedFormAsync(page, "/sqlos/auth/signup/phone-otp/verify", page.VerifyFields(SignupCodeHost.WrongSmsCode));
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var right = await host.PostHostedFormAsync(page, "/sqlos/auth/signup/phone-otp/verify", page.VerifyFields(SignupCodeHost.ApprovedSmsCode));
        right.StatusCode.Should().Be(HttpStatusCode.BadRequest, "one rejected check invalidates a phone challenge");

        host.Sms.ChecksFor(phoneNumber).Should().Be(1, "the invalidated challenge never reaches the provider again");
        await host.AssertPhoneChallengeRejectedAsync(phoneNumber);
    }

    [TestMethod]
    public async Task HostedPhoneSignup_ConcurrentWrongCodes_GetOneProviderCheck()
    {
        const int guesses = 6;
        var barrier = new FirstChallengeUpdateBarrier("SqlOSPhoneOtpChallenges", guesses);
        await using var host = await SignupCodeHost.CreateAsync(barrier: barrier);
        const string phoneNumber = "+12025550182";
        var page = await host.StartHostedPhoneSignupAsync(phoneNumber);

        barrier.Arm();
        var responses = await Task.WhenAll(Enumerable.Range(0, guesses).Select(_ =>
            host.PostHostedFormAsync(page, "/sqlos/auth/signup/phone-otp/verify", page.VerifyFields(SignupCodeHost.WrongSmsCode))));
        barrier.Disarm();

        barrier.Held.Should().Be(guesses, "every concurrent guess must reach its check write together");
        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.BadRequest);
        foreach (var response in responses)
        {
            response.Dispose();
        }

        using var right = await host.PostHostedFormAsync(page, "/sqlos/auth/signup/phone-otp/verify", page.VerifyFields(SignupCodeHost.ApprovedSmsCode));
        right.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        host.Sms.ChecksFor(phoneNumber).Should().Be(1, "concurrent guesses share the challenge's single provider check");
        await host.AssertPhoneChallengeRejectedAsync(phoneNumber);
    }

    [TestMethod]
    public async Task HeadlessEmailSignup_FiveWrongCodesInvalidateTheChallenge_AndTheRightCodeCreatesNoAccount()
    {
        await using var host = await SignupCodeHost.CreateAsync(headless: true);
        var email = UniqueEmail("headless-email");
        var requestId = await host.StartHeadlessAuthorizationAsync();
        var started = await host.PostHeadlessAsync("/sqlos/auth/headless/signup/email-otp/start", new
        {
            requestId,
            displayName = "Headless Email",
            email,
            organizationName = $"Headless Org {Guid.NewGuid():N}"
        });
        var challengeToken = started.ViewString("challengeToken");
        var signupToken = started.ViewString("signupToken");
        var code = host.LatestEmailCode(email);

        for (var attempt = 1; attempt <= MaxEmailAttempts; attempt++)
        {
            var wrong = await host.PostHeadlessAsync("/sqlos/auth/headless/signup/email-otp/verify", new
            {
                requestId,
                signupToken,
                challengeToken,
                code = WrongCode(code)
            });
            wrong.ViewString("error").Should().Be(InvalidCodeMessage);
        }

        var right = await host.PostHeadlessAsync("/sqlos/auth/headless/signup/email-otp/verify", new
        {
            requestId,
            signupToken,
            challengeToken,
            code
        });
        right.Type.Should().Be("view", "the exhausted challenge cannot complete the sign-up");
        right.ViewString("error").Should().Be(InvalidCodeMessage);

        await host.AssertEmailChallengeExhaustedAsync(email);
        await host.AssertNoAccountAsync(email);
    }

    [TestMethod]
    public async Task HeadlessPhoneSignup_ARejectedCodeInvalidatesTheChallenge_AndTheRightCodeCreatesNoAccount()
    {
        await using var host = await SignupCodeHost.CreateAsync(headless: true);
        const string phoneNumber = "+12025550183";
        var requestId = await host.StartHeadlessAuthorizationAsync();
        var started = await host.PostHeadlessAsync("/sqlos/auth/headless/signup/phone-otp/start", new
        {
            requestId,
            displayName = "Headless Phone",
            phoneNumber,
            organizationName = $"Headless Phone Org {Guid.NewGuid():N}"
        });
        var challengeToken = started.ViewString("challengeToken");
        var signupToken = started.ViewString("signupToken");

        var wrong = await host.PostHeadlessAsync("/sqlos/auth/headless/signup/phone-otp/verify", new
        {
            requestId,
            signupToken,
            challengeToken,
            code = SignupCodeHost.WrongSmsCode
        });
        wrong.ViewString("error").Should().Be(InvalidCodeMessage);
        var right = await host.PostHeadlessAsync("/sqlos/auth/headless/signup/phone-otp/verify", new
        {
            requestId,
            signupToken,
            challengeToken,
            code = SignupCodeHost.ApprovedSmsCode
        });
        right.Type.Should().Be("view");
        right.ViewString("error").Should().Be(InvalidCodeMessage);

        host.Sms.ChecksFor(phoneNumber).Should().Be(1);
        await host.AssertPhoneChallengeRejectedAsync(phoneNumber);
    }

    [TestMethod]
    public async Task PublicEmailSignup_FiveWrongCodesInvalidateTheChallenge_AndTheRightCodeCreatesNoAccount()
    {
        await using var host = await SignupCodeHost.CreateAsync();
        var email = UniqueEmail("public-email");
        SqlOSEmailOtpSignupStartResult started;
        await using (var scope = host.Fixture.App.Services.CreateAsyncScope())
        {
            started = await scope.ServiceProvider.GetRequiredService<SqlOSAuthService>().RequestEmailOtpSignupAsync(
                new SqlOSEmailOtpSignupStartRequest("Public Email", email, HostedAuthorizeTokenFixture.ClientId, $"Public Org {Guid.NewGuid():N}", null, null),
                SignupCodeHost.HttpContext());
        }

        var code = host.LatestEmailCode(email);
        for (var attempt = 1; attempt <= MaxEmailAttempts; attempt++)
        {
            await host.Invoking(h => h.VerifyPublicEmailSignupAsync(started, WrongCode(code)))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage(InvalidCodeMessage);
        }

        await host.Invoking(h => h.VerifyPublicEmailSignupAsync(started, code))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage(InvalidCodeMessage);

        await host.AssertEmailChallengeExhaustedAsync(email);
        await host.AssertNoAccountAsync(email);
    }

    [TestMethod]
    public async Task PublicPhoneSignup_ARejectedCodeInvalidatesTheChallenge_AndTheRightCodeCreatesNoAccount()
    {
        await using var host = await SignupCodeHost.CreateAsync();
        const string phoneNumber = "+12025550184";
        SqlOSPhoneOtpSignupStartResult started;
        await using (var scope = host.Fixture.App.Services.CreateAsyncScope())
        {
            started = await scope.ServiceProvider.GetRequiredService<SqlOSAuthService>().RequestPhoneOtpSignupAsync(
                new SqlOSPhoneOtpSignupStartRequest("Public Phone", phoneNumber, HostedAuthorizeTokenFixture.ClientId, $"Public Phone Org {Guid.NewGuid():N}", null, null),
                SignupCodeHost.HttpContext());
        }

        await host.Invoking(h => h.VerifyPublicPhoneSignupAsync(started, SignupCodeHost.WrongSmsCode))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage(InvalidCodeMessage);
        await host.Invoking(h => h.VerifyPublicPhoneSignupAsync(started, SignupCodeHost.ApprovedSmsCode))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage(InvalidCodeMessage);

        host.Sms.ChecksFor(phoneNumber).Should().Be(1);
        await host.AssertPhoneChallengeRejectedAsync(phoneNumber);
    }

    [TestMethod]
    public async Task SignupVerification_InsideACallerTransaction_IsRefusedBeforeAnAttemptIsCounted()
    {
        await using var host = await SignupCodeHost.CreateAsync();
        var email = UniqueEmail("in-transaction");
        SqlOSEmailOtpSignupStartResult started;
        await using (var scope = host.Fixture.App.Services.CreateAsyncScope())
        {
            started = await scope.ServiceProvider.GetRequiredService<SqlOSAuthService>().RequestEmailOtpSignupAsync(
                new SqlOSEmailOtpSignupStartRequest("In Transaction", email, HostedAuthorizeTokenFixture.ClientId, null, null, null),
                SignupCodeHost.HttpContext());
        }

        await using (var scope = host.Fixture.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var verify = async () => await scope.ServiceProvider.GetRequiredService<SqlOSEmailOtpService>().VerifySignupAsync(
                new SqlOSEmailOtpSignupVerifyRequest(started.SignupToken, started.ChallengeToken, WrongCode(host.LatestEmailCode(email))),
                expectedAuthorizationRequestId: null,
                requireAuthorizationRequestMatch: false);
            await verify.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*cannot run inside a database transaction*");
        }

        await using var verification = host.CreateDbContext();
        (await verification.Set<SqlOSEmailOtpChallenge>().AsNoTracking().SingleAsync(x => x.Email == email))
            .AttemptCount.Should().Be(0);
    }

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.test";

    private static string WrongCode(string code)
        => ((int.Parse(code) + 1) % 1_000_000).ToString("D6");

    /// <summary>
    /// A hosted SqlOS application (HostedAuthorizeTokenFixture) with email and phone sign-up codes
    /// on, a capturing email sender, and a fake SMS provider that approves one code.
    /// </summary>
    private sealed class SignupCodeHost : IAsyncDisposable
    {
        public const string ApprovedSmsCode = "246810";
        public const string WrongSmsCode = "135791";

        private readonly TestAuthEmailSender _emails;

        private SignupCodeHost(HostedAuthorizeTokenFixture fixture, TestAuthEmailSender emails, FakeSmsChannel sms)
        {
            Fixture = fixture;
            _emails = emails;
            Sms = sms;
        }

        public HostedAuthorizeTokenFixture Fixture { get; }
        public FakeSmsChannel Sms { get; }

        public static async Task<SignupCodeHost> CreateAsync(bool headless = false, FirstChallengeUpdateBarrier? barrier = null)
        {
            var emails = new TestAuthEmailSender { IsConfigured = true };
            var sms = new FakeSmsChannel();
            var fixture = await HostedAuthorizeTokenFixture.CreateAsync(
                "SignupCodes",
                configure: options =>
                {
                    var auth = options.AuthServer;
                    auth.SeedAuthPage(page =>
                    {
                        page.EnabledCredentialTypes = ["email_otp", "phone_otp"];
                        page.EnablePasswordSignup = false;
                    });
                    auth.EmailOtp.ResendCooldown = TimeSpan.Zero;
                    auth.ConfigurePhoneOtp(phone =>
                    {
                        phone.Enabled = true;
                        phone.TwilioAccountSid = "AC00000000000000000000000000000000";
                        phone.TwilioAuthToken = "test-token";
                        phone.TwilioVerifyServiceSid = "VA00000000000000000000000000000000";
                        phone.ResendCooldown = TimeSpan.Zero;
                    });
                    if (headless)
                    {
                        auth.UseHeadlessAuthPage(page => page.BuildUiUrl = context =>
                            $"https://app.example.test/auth?request={Uri.EscapeDataString(context.RequestId ?? string.Empty)}&view={context.View}");
                    }
                },
                configureServices: services =>
                {
                    services.RemoveAll<ISqlOSAuthEmailSender>();
                    services.AddSingleton<ISqlOSAuthEmailSender>(emails);
                    services.RemoveAll<ISqlOSEmailSender>();
                    services.AddSingleton<ISqlOSEmailSender>(emails);
                    services.RemoveAll<ISqlOSOtpDeliveryChannel>();
                    services.AddSingleton<ISqlOSOtpDeliveryChannel>(sms);
                    if (barrier != null)
                    {
                        services.ConfigureDbContext<TestSqlOSDbContext>(database => database.AddInterceptors(barrier));
                    }
                });
            return new SignupCodeHost(fixture, emails, sms);
        }

        public static DefaultHttpContext HttpContext()
        {
            var context = new DefaultHttpContext();
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("auth.example.test");
            context.Request.Headers.UserAgent = "SignupCodeAttemptIntegrationTests";
            context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.90");
            return context;
        }

        public string LatestEmailCode(string email)
            => EmailOwnershipServer.ExtractCode(_emails.Messages.Last(message => message.To == email));

        public async Task<HostedSignupPage> StartHostedEmailSignupAsync(string email)
        {
            var started = await Fixture.StartAuthorizeAsync("openid profile email");
            using var response = await PostHostedFormAsync(started, "/sqlos/auth/signup/email-otp/start", new Dictionary<string, string>
            {
                ["displayName"] = "Signup Code User",
                ["email"] = email,
                ["organizationName"] = $"Signup Code Org {Guid.NewGuid():N}"
            });
            return await ReadVerifyPageAsync(started, response, "email", email);
        }

        public async Task<HostedSignupPage> StartHostedPhoneSignupAsync(string phoneNumber)
        {
            var started = await Fixture.StartAuthorizeAsync("openid profile email");
            using var response = await PostHostedFormAsync(started, "/sqlos/auth/signup/phone-otp/start", new Dictionary<string, string>
            {
                ["displayName"] = "Signup Phone User",
                ["phoneNumber"] = phoneNumber,
                ["organizationName"] = $"Signup Phone Org {Guid.NewGuid():N}"
            });
            return await ReadVerifyPageAsync(started, response, "phoneNumber", phoneNumber);
        }

        public Task<HttpResponseMessage> PostHostedFormAsync(
            HostedAuthorizeStart started,
            string path,
            IDictionary<string, string> fields)
        {
            var form = new Dictionary<string, string>(fields)
            {
                ["requestId"] = started.RequestId,
                ["__RequestVerificationToken"] = started.AntiforgeryToken
            };
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
            request.Headers.TryAddWithoutValidation("Cookie", started.AntiforgeryCookie);
            request.Headers.TryAddWithoutValidation("Origin", HostedAuthorizeTokenFixture.TrustedOrigin);
            return Fixture.Client.SendAsync(request);
        }

        public Task<HttpResponseMessage> PostHostedFormAsync(HostedSignupPage page, string path, IDictionary<string, string> fields)
            => PostHostedFormAsync(page.Started, path, fields);

        public async Task<string> StartHeadlessAuthorizationAsync()
        {
            using var authorize = await Fixture.AuthorizeRawAsync(HttpMethod.Get, "openid profile email");
            authorize.Response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var requestId = QueryHelpers.ParseQuery(authorize.Response.Headers.Location!.Query)["request"].ToString();
            requestId.Should().NotBeNullOrWhiteSpace();
            return requestId;
        }

        public async Task<HeadlessResult> PostHeadlessAsync(string path, object body)
        {
            using var response = await Fixture.Client.PostAsJsonAsync(path, body);
            var json = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, "body: {0}", json);
            return new HeadlessResult(JsonDocument.Parse(json).RootElement.Clone());
        }

        public async Task VerifyPublicEmailSignupAsync(SqlOSEmailOtpSignupStartResult started, string code)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SqlOSAuthService>().VerifyEmailOtpSignupAsync(
                new SqlOSEmailOtpSignupVerifyRequest(started.SignupToken, started.ChallengeToken, code),
                HttpContext());
        }

        public async Task VerifyPublicPhoneSignupAsync(SqlOSPhoneOtpSignupStartResult started, string code)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SqlOSAuthService>().VerifyPhoneOtpSignupAsync(
                new SqlOSPhoneOtpSignupVerifyRequest(started.SignupToken, started.ChallengeToken, code),
                HttpContext());
        }

        public TestSqlOSDbContext CreateDbContext()
        {
            using var scope = Fixture.App.Services.CreateScope();
            var connectionString = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>().Database.GetConnectionString()!;
            return new TestSqlOSDbContext(new DbContextOptionsBuilder<TestSqlOSDbContext>()
                .UseTestProvider(connectionString)
                .Options);
        }

        public async Task<int> CountAuditAsync(string eventType)
        {
            await using var db = CreateDbContext();
            return await db.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == eventType);
        }

        /// <summary>
        /// Exactly MaxAttempts comparisons were counted and audited, the last one invalidated the
        /// challenge, and nothing consumed it.
        /// </summary>
        public async Task AssertEmailChallengeExhaustedAsync(string email)
        {
            await using var db = CreateDbContext();
            var challenge = await db.Set<SqlOSEmailOtpChallenge>().AsNoTracking().SingleAsync(x => x.Email == email);
            challenge.AttemptCount.Should().Be(MaxEmailAttempts);
            challenge.InvalidatedAt.Should().NotBeNull();
            challenge.InvalidatedReason.Should().Be("max_attempts");
            challenge.ConsumedAt.Should().BeNull();

            var failures = await db.Set<SqlOSAuditEvent>()
                .Where(x => x.EventType == "email_otp.verify_failed")
                .Select(x => x.MetadataJson)
                .ToListAsync();
            failures.Should().HaveCount(MaxEmailAttempts, "every counted wrong code is audited, and no more");
            failures.Count(metadata => metadata!.Contains("max_attempts", StringComparison.Ordinal)).Should().Be(1);
            (await db.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "email_otp.verify_succeeded")).Should().Be(0);
        }

        public async Task AssertNoAccountAsync(string email)
        {
            await using var db = CreateDbContext();
            (await db.Set<SqlOSUserEmail>().CountAsync(x => x.Email == email)).Should().Be(0);
            (await db.Set<SqlOSUser>().CountAsync(x => x.DefaultEmail == email)).Should().Be(0);
            (await db.Set<SqlOSTemporaryToken>().SingleAsync(x => x.Purpose == "email_otp_signup")).ConsumedAt.Should().BeNull();
        }

        /// <summary>The phone challenge's one check was rejected, recorded, and left no account.</summary>
        public async Task AssertPhoneChallengeRejectedAsync(string phoneNumber)
        {
            await using var db = CreateDbContext();
            var challenge = await db.Set<SqlOSPhoneOtpChallenge>().AsNoTracking().SingleAsync(x => x.Purpose == "signup");
            challenge.AttemptCount.Should().Be(1);
            challenge.InvalidatedAt.Should().NotBeNull();
            challenge.ConsumedAt.Should().BeNull();
            (await db.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "phone_otp.verify_failed")).Should().Be(1);
            (await db.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "phone_otp.verify_succeeded")).Should().Be(0);
            (await db.Set<SqlOSUserPhoneNumber>().CountAsync(x => x.PhoneNumber == phoneNumber)).Should().Be(0);
            (await db.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "user.signup.phone_otp")).Should().Be(0);
        }

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();

        private static async Task<HostedSignupPage> ReadVerifyPageAsync(
            HostedAuthorizeStart started,
            HttpResponseMessage response,
            string identifierField,
            string identifier)
        {
            var html = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, "the sign-up start renders the code form: {0}", html);
            return new HostedSignupPage(
                started,
                ReadHiddenInput(html, "challengeToken"),
                ReadHiddenInput(html, "signupToken"),
                identifierField,
                identifier);
        }

        private static string ReadHiddenInput(string html, string name)
        {
            var match = Regex.Match(html, $@"name=""{Regex.Escape(name)}"" value=""([^""]+)""", RegexOptions.CultureInvariant);
            match.Success.Should().BeTrue($"the verify form carries {name}");
            return WebUtility.HtmlDecode(match.Groups[1].Value);
        }
    }

    private sealed record HostedSignupPage(
        HostedAuthorizeStart Started,
        string ChallengeToken,
        string SignupToken,
        string IdentifierField,
        string Identifier)
    {
        public string CodeVerifier => Started.CodeVerifier;

        public Dictionary<string, string> VerifyFields(string code) => new()
        {
            [IdentifierField] = Identifier,
            ["challengeToken"] = ChallengeToken,
            ["signupToken"] = SignupToken,
            ["code"] = code
        };
    }

    private sealed record HeadlessResult(JsonElement Root)
    {
        public string Type => Root.GetProperty("type").GetString()!;

        public string? ViewString(string property)
            => Root.GetProperty("viewModel").TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>Stands in for Twilio Verify: approves one code and counts every check per number.</summary>
    private sealed class FakeSmsChannel : ISqlOSOtpDeliveryChannel
    {
        private readonly ConcurrentQueue<string> _checks = new();
        private int _starts;

        public int ChecksFor(string phoneNumber) => _checks.Count(number => number == phoneNumber);

        public Task<SqlOSOtpDeliveryStartResult> StartAsync(
            string e164PhoneNumber,
            SqlOSOtpDeliveryContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SqlOSOtpDeliveryStartResult(true, "test_verify", $"ve-{Interlocked.Increment(ref _starts)}", "pending"));

        public Task<SqlOSOtpDeliveryCheckResult> CheckAsync(
            string e164PhoneNumber,
            string code,
            SqlOSOtpDeliveryContext context,
            CancellationToken cancellationToken = default)
        {
            _checks.Enqueue(e164PhoneNumber);
            var approved = string.Equals(code, SignupCodeHost.ApprovedSmsCode, StringComparison.Ordinal);
            return Task.FromResult(new SqlOSOtpDeliveryCheckResult(
                approved,
                "test_verify",
                context.ProviderChallengeId,
                approved ? "approved" : "denied",
                approved ? null : "bad_code"));
        }
    }

    /// <summary>
    /// Makes the check-then-write race deterministic: once armed, each DbContext is held at its
    /// first UPDATE of the challenge table until every participant is waiting there (or ten
    /// seconds pass), so all concurrent verifications have read the challenge before any writes.
    /// </summary>
    private sealed class FirstChallengeUpdateBarrier : DbCommandInterceptor
    {
        private readonly Regex _statement;
        private readonly int _participants;
        private readonly ConcurrentDictionary<Guid, byte> _held = new();
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _armed;

        public FirstChallengeUpdateBarrier(string table, int participants)
        {
            // SQL Server set-based updates read "UPDATE [c] SET ... FROM [dbo].[Table] AS [c]".
            _statement = new Regex($@"\bUPDATE\b[\s\S]*\b{Regex.Escape(table)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            _participants = participants;
        }

        public int Held => _held.Count;

        public void Arm() => _armed = true;

        public void Disarm() => _armed = false;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await HoldAsync(command, eventData);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await HoldAsync(command, eventData);
            return result;
        }

        private async Task HoldAsync(DbCommand command, CommandEventData eventData)
        {
            if (!_armed
                || eventData.Context is null
                || !_statement.IsMatch(command.CommandText)
                || !_held.TryAdd(eventData.Context.ContextId.InstanceId, 0))
            {
                return;
            }

            if (_held.Count >= _participants)
            {
                _released.TrySetResult();
            }

            await Task.WhenAny(_released.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        }
    }
}
