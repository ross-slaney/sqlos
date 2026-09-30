using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Errors;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Email.Interfaces;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// Real-SQL, wire-level proof that direct login is first-party only (#419). The direct routes return
/// tokens in the response body, so there is no user-agent redirect where a consent screen could
/// appear. A CIMD, DCR, or admin-created third-party client is refused with a generic
/// <c>invalid_client</c> error at every start route (no provider state, email, or session), and at
/// every completion route even when its artifact was minted before the gate existed. Each refusal
/// writes one audit event. First-party clients keep working on every route.
/// </summary>
[TestClass]
public sealed class DirectLoginFirstPartyIntegrationTests
{
    private const string Origin = HostedAuthorizeTokenFixture.TrustedOrigin;
    private const string Password = HostedAuthorizeTokenFixture.Password;
    private const string CimdClientId = "https://evil.example.test/client.json";
    private const string AttackerRedirect = "https://evil.example.test/cb";
    private const string AdminClientId = "admin-third-party";
    private const string RejectedEvent = "oauth.direct_login.rejected";

    public enum ThirdPartyKind
    {
        Cimd,
        Dcr,
        AdminCreated
    }

    [DataTestMethod]
    [DataRow(ThirdPartyKind.Cimd)]
    [DataRow(ThirdPartyKind.Dcr)]
    [DataRow(ThirdPartyKind.AdminCreated)]
    public async Task ThirdPartyClient_IsRejectedAtEveryDirectLoginStart_BeforeAnythingIsMinted(ThirdPartyKind kind)
    {
        await using var host = await DirectLoginHost.CreateAsync();
        var client = await host.RegisterThirdPartyClientAsync(kind);
        var victim = await host.CreateUserAsync("victim");
        var signupEmail = $"attacker-signup-{Guid.NewGuid():N}@example.test";
        var connectionId = await host.GetGoogleConnectionIdAsync();

        using (new AssertionScope())
        {
            // A third-party UI that collected the victim's password: OAuth 2.1 removed this grant.
            await AssertRejectedAsync(host, client, "/sqlos/auth/password/login", () => host.Client.PostAsJsonAsync(
                "/sqlos/auth/password/login",
                new { email = victim.Email, password = Password, clientId = client.ClientId }));

            await AssertRejectedAsync(host, client, "/sqlos/auth/signup", () => host.Client.PostAsJsonAsync(
                "/sqlos/auth/signup",
                new { displayName = "Attacker Signup", email = signupEmail, password = Password, clientId = client.ClientId }));

            // Otherwise SqlOS would email a code or link under the host's branding for the client's UI to collect.
            await AssertRejectedAsync(host, client, "/sqlos/auth/email-otp/start", () => host.Client.PostAsJsonAsync(
                "/sqlos/auth/email-otp/start",
                new { email = victim.Email, clientId = client.ClientId }));

            await AssertRejectedAsync(host, client, "/sqlos/auth/magic-link/start", () => host.Client.PostAsJsonAsync(
                "/sqlos/auth/magic-link/start",
                new { email = victim.Email, clientId = client.ClientId }));

            // Step 2 of the social-link attack: no provider URL, so no bearer state to send the victim.
            await AssertRejectedAsync(host, client, "/sqlos/auth/oidc/authorization-url", () => host.Client.PostAsJsonAsync(
                "/sqlos/auth/oidc/authorization-url",
                new
                {
                    connectionId,
                    clientId = client.ClientId,
                    redirectUri = AttackerRedirect,
                    state = "attacker-state",
                    codeChallenge = HostedAuthorizeTokenFixture.CreateCodeChallenge(HostedAuthorizeTokenFixture.CreateCodeVerifier()),
                    codeChallengeMethod = "S256"
                }));

            host.EmailSender.Messages.Should().BeEmpty("no code or link is emailed for a third-party client");
            (await host.QueryAsync(db => db.Set<SqlOSEmailOtpChallenge>().CountAsync(x => x.ClientApplicationId == client.Id)))
                .Should().Be(0);
            (await host.QueryAsync(db => db.Set<SqlOSTemporaryToken>().CountAsync(x => x.ClientApplicationId == client.Id)))
                .Should().Be(0, "no magic link, provider state, pending-auth, or MFA token is minted for the client");
            (await host.QueryAsync(db => db.Set<SqlOSSession>().CountAsync(x => x.ClientApplicationId == client.Id || x.UserId == victim.Id)))
                .Should().Be(0);
            (await host.FindUserIdAsync(signupEmail)).Should().BeNull("a rejected signup creates no account");
        }
    }

    [DataTestMethod]
    [DataRow(ThirdPartyKind.Cimd)]
    [DataRow(ThirdPartyKind.Dcr)]
    [DataRow(ThirdPartyKind.AdminCreated)]
    public async Task ThirdPartyClient_CannotRedeemDirectLoginArtifactsIssuedBeforeTheGate(ThirdPartyKind kind)
    {
        await using var host = await DirectLoginHost.CreateAsync();
        var client = await host.RegisterThirdPartyClientAsync(kind);
        var connectionId = await host.GetGoogleConnectionIdAsync();

        // Each artifact below is minted while the client is temporarily first-party, which is exactly
        // what an in-flight artifact from before this release looks like. The client is third-party again
        // before the completion call, so only the completion-side gates stand between it and tokens.
        var otpUser = await host.CreateUserAsync("otp");
        var challengeToken = await host.WhileFirstPartyAsync(client, async () =>
        {
            using var response = await host.Client.PostAsJsonAsync(
                "/sqlos/auth/email-otp/start",
                new { email = otpUser.Email, clientId = client.ClientId });
            return await ReadStringAsync(response, "challengeToken");
        });
        var otpCode = host.LatestOtpCode(otpUser.Email);

        var linkUser = await host.CreateUserAsync("link");
        await host.WhileFirstPartyAsync(client, async () =>
        {
            using var response = await host.Client.PostAsJsonAsync(
                "/sqlos/auth/magic-link/start",
                new { email = linkUser.Email, clientId = client.ClientId });
            response.StatusCode.Should().Be(HttpStatusCode.OK, "body: {0}", await response.Content.ReadAsStringAsync());
            return true;
        });
        var linkToken = host.LatestMagicLinkToken(linkUser.Email);

        var orgUser = await host.CreateUserAsync("orgs", organizationCount: 2);
        var (pendingAuthToken, selectedOrganizationId) = await host.WhileFirstPartyAsync(client, async () =>
        {
            using var response = await host.Client.PostAsJsonAsync(
                "/sqlos/auth/password/login",
                new { email = orgUser.Email, password = Password, clientId = client.ClientId });
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (
                json.RootElement.GetProperty("pendingAuthToken").GetString()!,
                json.RootElement.GetProperty("organizations")[0].GetProperty("id").GetString()!);
        });

        var mfaUser = await host.CreateUserAsync("mfa");
        var totpSecret = await host.EnrollTotpAsync(mfaUser.Id);
        var mfaToken = await host.WhileFirstPartyAsync(client, async () =>
        {
            using var response = await host.Client.PostAsJsonAsync(
                "/sqlos/auth/password/login",
                new { email = mfaUser.Email, password = Password, clientId = client.ClientId });
            return await ReadStringAsync(response, "mfaToken");
        });

        // Forced TOTP enrollment: the organization requires MFA and these users have no authenticator.
        var mfaOrganizationId = await host.CreateOrganizationRequiringMfaAsync();
        var enrollStartUser = await host.CreateUserAsync("mfa-enroll-start", memberOf: mfaOrganizationId);
        var enrollStartMfaToken = await host.WhileFirstPartyAsync(client, async () =>
        {
            using var response = await host.Client.PostAsJsonAsync(
                "/sqlos/auth/password/login",
                new { email = enrollStartUser.Email, password = Password, clientId = client.ClientId });
            return await ReadStringAsync(response, "mfaToken");
        });

        var enrollVerifyUser = await host.CreateUserAsync("mfa-enroll-verify", memberOf: mfaOrganizationId);
        var (enrollVerifyMfaToken, enrollmentToken, enrollmentSecret) = await host.WhileFirstPartyAsync(client, async () =>
        {
            using var login = await host.Client.PostAsJsonAsync(
                "/sqlos/auth/password/login",
                new { email = enrollVerifyUser.Email, password = Password, clientId = client.ClientId });
            var token = await ReadStringAsync(login, "mfaToken");
            using var started = await host.Client.PostAsJsonAsync(
                "/sqlos/auth/mfa/challenge/totp/enroll/start",
                new { mfaToken = token });
            return (token, await ReadStringAsync(started, "enrollmentToken"), await ReadStringAsync(started, "secret"));
        });

        // Steps 3-5 of the social-link attack with provider state that predates the gate.
        var callbackEmail = $"social-callback-{Guid.NewGuid():N}@example.test";
        var inFlightProviderUrl = await host.WhileFirstPartyAsync(
            client,
            () => host.StartSocialLoginAsync(connectionId, client.ClientId, HostedAuthorizeTokenFixture.CreateCodeVerifier(), AttackerRedirect));

        var exchangeEmail = $"social-exchange-{Guid.NewGuid():N}@example.test";
        var exchangeVerifier = HostedAuthorizeTokenFixture.CreateCodeVerifier();
        var preGateCode = await host.WhileFirstPartyAsync(client, async () =>
        {
            var providerUrl = await host.StartSocialLoginAsync(connectionId, client.ClientId, exchangeVerifier, AttackerRedirect);
            using var callback = await host.CompleteProviderCallbackAsync(providerUrl, exchangeEmail);
            return QueryHelpers.ParseQuery(callback.Headers.Location!.Query)["code"].ToString();
        });
        var exchangeUserId = await host.FindUserIdAsync(exchangeEmail);

        using (new AssertionScope())
        {
            await AssertRejectedAsync(host, client, "/sqlos/auth/email-otp/verify", () => host.Client.PostAsJsonAsync(
                "/sqlos/auth/email-otp/verify",
                new { challengeToken, code = otpCode }));

            await AssertRejectedAsync(host, client, "/sqlos/auth/magic-link/complete", () => host.Client.PostAsJsonAsync(
                "/sqlos/auth/magic-link/complete",
                new { token = linkToken }));

            await AssertRejectedAsync(host, client, "/sqlos/auth/select-organization", () => host.Client.PostAsJsonAsync(
                "/sqlos/auth/select-organization",
                new { pendingAuthToken, organizationId = selectedOrganizationId }));

            var totpCode = await host.NextTotpCodeAsync(totpSecret);
            await AssertRejectedAsync(host, client, "/sqlos/auth/mfa/challenge/verify", () => host.Client.PostAsJsonAsync(
                "/sqlos/auth/mfa/challenge/verify",
                new { mfaToken, code = totpCode }));

            await AssertRejectedAsync(host, client, "/sqlos/auth/mfa/challenge/totp/enroll/start", () => host.Client.PostAsJsonAsync(
                "/sqlos/auth/mfa/challenge/totp/enroll/start",
                new { mfaToken = enrollStartMfaToken }));
            (await host.QueryAsync(db => db.Set<SqlOSUserAuthenticator>().CountAsync(x => x.UserId == enrollStartUser.Id)))
                .Should().Be(0, "no authenticator enrollment starts for the client");

            var enrollmentCode = await host.TotpCodeAsync(enrollmentSecret);
            await AssertRejectedAsync(host, client, "/sqlos/auth/mfa/challenge/totp/enroll/verify", () => host.Client.PostAsJsonAsync(
                "/sqlos/auth/mfa/challenge/totp/enroll/verify",
                new { enrollmentToken, code = enrollmentCode, mfaToken = enrollVerifyMfaToken }));
            (await host.QueryAsync(db => db.Set<SqlOSUserAuthenticator>().CountAsync(x => x.UserId == enrollVerifyUser.Id && x.IsConfirmed)))
                .Should().Be(0, "the client cannot confirm an authenticator on the user's account");

            // The victim's browser comes back from the provider: no upstream login, no code, just an error.
            var callbackEarlier = await host.ListRejectionIdsAsync(client);
            using (var callback = await host.CompleteProviderCallbackAsync(inFlightProviderUrl, callbackEmail))
            {
                callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
                var location = callback.Headers.Location;
                location.Should().NotBeNull();
                if (location != null)
                {
                    location.AbsoluteUri.Should().StartWith(AttackerRedirect);
                    var query = QueryHelpers.ParseQuery(location.Query);
                    query.ContainsKey("code").Should().BeFalse("the callback must not mint an application code for the client");
                    query["error"].ToString().Should().NotBeNullOrWhiteSpace();
                }
            }

            await AssertOneRejectionAuditedAsync(host, client, "/sqlos/auth/oidc/callback", callbackEarlier);
            (await host.FindUserIdAsync(callbackEmail)).Should().BeNull("the upstream login is not completed for a rejected client");

            // A code minted before the gate still cannot be redeemed.
            await AssertRejectedAsync(host, client, "/sqlos/auth/oidc/exchange", () => host.Client.PostAsJsonAsync(
                "/sqlos/auth/oidc/exchange",
                new { code = preGateCode, clientId = client.ClientId, redirectUri = AttackerRedirect, codeVerifier = exchangeVerifier }));

            foreach (var userId in new[] { otpUser.Id, linkUser.Id, orgUser.Id, mfaUser.Id, enrollStartUser.Id, enrollVerifyUser.Id, exchangeUserId })
            {
                (await host.QueryAsync(db => db.Set<SqlOSSession>().CountAsync(x => x.UserId == userId)))
                    .Should().Be(0, "a rejected direct login creates no session");
            }

            (await host.QueryAsync(db => db.Set<SqlOSSession>().CountAsync(x => x.ClientApplicationId == client.Id)))
                .Should().Be(0);
        }
    }

    [TestMethod]
    public async Task ThirdPartyClient_CannotRedeemAnAuthorizeEmailCodeAtTheDirectRoute()
    {
        // The consent-gated /authorize flow emails a code for the third-party client; its own UI holds
        // the challenge token. Redeeming that code at the direct route must not skip consent.
        await using var host = await DirectLoginHost.CreateAsync();
        var client = await host.RegisterThirdPartyClientAsync(ThirdPartyKind.Cimd);
        var victim = await host.CreateUserAsync("authorize-otp");
        var started = await host.Fixture.StartAuthorizeAsync("openid", clientId: client.ClientId, redirectUri: AttackerRedirect);
        using var otpStart = new HttpRequestMessage(HttpMethod.Post, "/sqlos/auth/login/email-otp/start")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["requestId"] = started.RequestId,
                ["email"] = victim.Email,
                ["__RequestVerificationToken"] = started.AntiforgeryToken
            })
        };
        otpStart.Headers.TryAddWithoutValidation("Cookie", started.AntiforgeryCookie);
        otpStart.Headers.TryAddWithoutValidation("Origin", Origin);
        using var otpPage = await host.Client.SendAsync(otpStart);
        var html = await otpPage.Content.ReadAsStringAsync();
        otpPage.StatusCode.Should().Be(HttpStatusCode.OK, "body: {0}", html);
        var challengeToken = WebUtility.HtmlDecode(
            Regex.Match(html, @"name=""challengeToken"" value=""([^""]*)""").Groups[1].Value);
        challengeToken.Should().NotBeNullOrWhiteSpace();

        await AssertRejectedAsync(host, client, "/sqlos/auth/email-otp/verify", () => host.Client.PostAsJsonAsync(
            "/sqlos/auth/email-otp/verify",
            new { challengeToken, code = host.LatestOtpCode(victim.Email) }));
        (await host.QueryAsync(db => db.Set<SqlOSSession>().CountAsync(x => x.UserId == victim.Id)))
            .Should().Be(0);
    }

    [TestMethod]
    public async Task CodeOnlySignupApis_RefuseThirdPartyClientBeforeTheSignupTransaction_AndKeepTheAudit()
    {
        // No SqlOS route maps these; host code calls them. On SQL they run in a transaction, so the
        // refusal has to happen before it opens or its audit event would roll back with it.
        await using var host = await DirectLoginHost.CreateAsync();
        var client = await host.RegisterThirdPartyClientAsync(ThirdPartyKind.AdminCreated);
        var http = new DefaultHttpContext();
        http.Request.Path = "/app/signup";

        string invitationToken;
        await using (var scope = host.Fixture.App.Services.CreateAsyncScope())
        {
            var organization = await scope.ServiceProvider.GetRequiredService<SqlOSAdminService>()
                .CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Invite org {Guid.NewGuid():N}", null));
            var invite = await scope.ServiceProvider.GetRequiredService<SqlOSInvitationService>().CreateEmailInvitationAsync(
                new SqlOSCreateEmailInvitationRequest(organization.Id, $"invitee-{Guid.NewGuid():N}@example.test", "member"),
                http);
            invitationToken = Uri.UnescapeDataString(Regex.Match(invite.InviteUrl!, @"[?&]token=([^&]+)").Groups[1].Value);
        }

        var signupEmail = $"code-only-signup-{Guid.NewGuid():N}@example.test";
        var signupStart = await host.WhileFirstPartyAsync(client, async () =>
        {
            await using var scope = host.Fixture.App.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<SqlOSAuthService>().RequestEmailOtpSignupAsync(
                new SqlOSEmailOtpSignupStartRequest("Code Only Signup", signupEmail, client.ClientId, null, null, null),
                http);
        });
        var usersBefore = await host.QueryAsync(db => db.Set<SqlOSUser>().CountAsync());

        await using (var scope = host.Fixture.App.Services.CreateAsyncScope())
        {
            var auth = scope.ServiceProvider.GetRequiredService<SqlOSAuthService>();
            var invitationSignup = async () => await auth.AcceptEmailInvitationSignupAsync(
                new SqlOSAcceptEmailInvitationSignupRequest(invitationToken, "Invitee", client.ClientId),
                http);
            (await invitationSignup.Should().ThrowAsync<SqlOSPublicAuthException>()).Which.Error.Should().Be("invalid_client");
        }

        await using (var scope = host.Fixture.App.Services.CreateAsyncScope())
        {
            var auth = scope.ServiceProvider.GetRequiredService<SqlOSAuthService>();
            var emailSignup = async () => await auth.VerifyEmailOtpSignupAsync(
                new SqlOSEmailOtpSignupVerifyRequest(signupStart.SignupToken, signupStart.ChallengeToken, host.LatestOtpCode(signupEmail)),
                http);
            (await emailSignup.Should().ThrowAsync<SqlOSPublicAuthException>()).Which.Error.Should().Be("invalid_client");
        }

        (await host.QueryAsync(db => db.Set<SqlOSUser>().CountAsync())).Should().Be(usersBefore, "no account is created");
        (await host.QueryAsync(db => db.Set<SqlOSInvitation>().CountAsync(x => x.AcceptedAt != null))).Should().Be(0);
        var rejections = await host.ListRejectionsAsync(client);
        rejections.Should().HaveCount(2, "each refusal is audited once, outside the signup transaction");
        rejections.Should().OnlyContain(item => item.Route == "/app/signup");
    }

    [TestMethod]
    public async Task FirstPartyClient_KeepsWorkingOnEveryDirectLoginRoute()
    {
        await using var host = await DirectLoginHost.CreateAsync();
        const string clientId = HostedAuthorizeTokenFixture.ClientId;

        var passwordUser = await host.CreateUserAsync("fp-password");
        using (var login = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/password/login",
            new { email = passwordUser.Email, password = Password, clientId }))
        {
            await AssertTokensAsync(login, "tokens");
        }

        using (var signup = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/signup",
            new { displayName = "First Party Signup", email = $"fp-signup-{Guid.NewGuid():N}@example.test", password = Password, clientId }))
        {
            await AssertTokensAsync(signup, "tokens");
        }

        var otpUser = await host.CreateUserAsync("fp-otp");
        string challengeToken;
        using (var started = await host.Client.PostAsJsonAsync("/sqlos/auth/email-otp/start", new { email = otpUser.Email, clientId }))
        {
            challengeToken = await ReadStringAsync(started, "challengeToken");
        }

        using (var verified = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/email-otp/verify",
            new { challengeToken, code = host.LatestOtpCode(otpUser.Email) }))
        {
            await AssertTokensAsync(verified, "tokens");
        }

        var linkUser = await host.CreateUserAsync("fp-link");
        using (var started = await host.Client.PostAsJsonAsync("/sqlos/auth/magic-link/start", new { email = linkUser.Email, clientId }))
        {
            started.StatusCode.Should().Be(HttpStatusCode.OK, "body: {0}", await started.Content.ReadAsStringAsync());
        }

        using (var completed = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/magic-link/complete",
            new { token = host.LatestMagicLinkToken(linkUser.Email) }))
        {
            await AssertTokensAsync(completed, "tokens");
        }

        var orgUser = await host.CreateUserAsync("fp-orgs", organizationCount: 2);
        string pendingAuthToken;
        string organizationId;
        using (var login = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/password/login",
            new { email = orgUser.Email, password = Password, clientId }))
        {
            using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            pendingAuthToken = json.RootElement.GetProperty("pendingAuthToken").GetString()!;
            organizationId = json.RootElement.GetProperty("organizations")[0].GetProperty("id").GetString()!;
        }

        using (var selected = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/select-organization",
            new { pendingAuthToken, organizationId }))
        {
            await AssertTokensAsync(selected, "tokens");
        }

        var mfaUser = await host.CreateUserAsync("fp-mfa");
        var totpSecret = await host.EnrollTotpAsync(mfaUser.Id);
        string mfaToken;
        using (var login = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/password/login",
            new { email = mfaUser.Email, password = Password, clientId }))
        {
            mfaToken = await ReadStringAsync(login, "mfaToken");
        }

        using (var verified = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/mfa/challenge/verify",
            new { mfaToken, code = await host.NextTotpCodeAsync(totpSecret) }))
        {
            await AssertTokensAsync(verified, "tokens");
        }

        var enrollUser = await host.CreateUserAsync("fp-mfa-enroll", memberOf: await host.CreateOrganizationRequiringMfaAsync());
        string enrollMfaToken;
        using (var login = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/password/login",
            new { email = enrollUser.Email, password = Password, clientId }))
        {
            enrollMfaToken = await ReadStringAsync(login, "mfaToken");
        }

        string enrollmentToken;
        string enrollmentSecret;
        using (var started = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/mfa/challenge/totp/enroll/start",
            new { mfaToken = enrollMfaToken }))
        {
            enrollmentToken = await ReadStringAsync(started, "enrollmentToken");
            enrollmentSecret = await ReadStringAsync(started, "secret");
        }

        using (var enrolled = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/mfa/challenge/totp/enroll/verify",
            new { enrollmentToken, code = await host.TotpCodeAsync(enrollmentSecret), mfaToken = enrollMfaToken }))
        {
            await AssertTokensAsync(enrolled, "tokens");
        }

        var connectionId = await host.GetGoogleConnectionIdAsync();
        var verifier = HostedAuthorizeTokenFixture.CreateCodeVerifier();
        var providerUrl = await host.StartSocialLoginAsync(connectionId, clientId, verifier, HostedAuthorizeTokenFixture.RedirectUri);
        string code;
        using (var callback = await host.CompleteProviderCallbackAsync(providerUrl, $"fp-social-{Guid.NewGuid():N}@example.test"))
        {
            callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
            callback.Headers.Location!.AbsoluteUri.Should().StartWith(HostedAuthorizeTokenFixture.RedirectUri);
            code = QueryHelpers.ParseQuery(callback.Headers.Location.Query)["code"].ToString();
        }

        using (var exchanged = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/oidc/exchange",
            new { code, clientId, redirectUri = HostedAuthorizeTokenFixture.RedirectUri, codeVerifier = verifier }))
        {
            await AssertTokensAsync(exchanged, "tokens");
        }

        (await host.QueryAsync(db => db.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == RejectedEvent)))
            .Should().Be(0, "first-party direct login is never rejected");
    }

    private static async Task AssertRejectedAsync(
        DirectLoginHost host,
        ThirdPartyClient client,
        string route,
        Func<Task<HttpResponseMessage>> send)
    {
        var earlier = await host.ListRejectionIdsAsync(client);
        using var response = await send();
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} must refuse a third-party client (body: {1})", route, body);
        body.Should().Contain("\"error\":\"invalid_client\"", "{0} answers with the generic client error", route);
        body.Should().NotContain("accessToken", "{0} must not return tokens", route)
            .And.NotContain("pendingAuthToken")
            .And.NotContain("mfaToken")
            .And.NotContain("challengeToken")
            .And.NotContain("authorizationUrl");
        await AssertOneRejectionAuditedAsync(host, client, route, earlier);
    }

    private static async Task AssertOneRejectionAuditedAsync(
        DirectLoginHost host,
        ThirdPartyClient client,
        string route,
        IReadOnlyCollection<string> earlier)
    {
        var added = (await host.ListRejectionsAsync(client))
            .Where(item => !earlier.Contains(item.Id))
            .ToList();
        added.Should().ContainSingle("{0} writes exactly one {1} event", route, RejectedEvent);
        if (added.Count == 1)
        {
            added[0].Route.Should().Be(route);
        }
    }

    private static async Task AssertTokensAsync(HttpResponseMessage response, string tokensProperty)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "body: {0}", body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty(tokensProperty).GetProperty("accessToken").GetString()
            .Should().NotBeNullOrWhiteSpace("body: {0}", body);
    }

    private static async Task<string> ReadStringAsync(HttpResponseMessage response, string property)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "body: {0}", body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty(property).GetString()
            ?? throw new InvalidOperationException($"Response did not include '{property}': {body}");
    }

    public sealed record ThirdPartyClient(string Id, string ClientId);

    private sealed record TestUser(string Id, string Email);

    private sealed record Rejection(string Id, string? ClientId, string? Route);

    private sealed class DirectLoginHost : IAsyncDisposable
    {
        private static readonly Regex OtpCodePattern = new(@"code is (\d{4,8})", RegexOptions.Compiled);
        private static readonly Regex MagicLinkTokenPattern = new(@"[?&]token=([^\s&]+)", RegexOptions.Compiled);

        private DirectLoginHost(HostedAuthorizeTokenFixture fixture, TestAuthEmailSender emailSender)
        {
            Fixture = fixture;
            EmailSender = emailSender;
        }

        public HostedAuthorizeTokenFixture Fixture { get; }

        public TestAuthEmailSender EmailSender { get; }

        public HttpClient Client => Fixture.Client;

        public static async Task<DirectLoginHost> CreateAsync()
        {
            var emailSender = new TestAuthEmailSender { IsConfigured = true };
            var fixture = await HostedAuthorizeTokenFixture.CreateAsync(
                "DirectLogin",
                configure: options =>
                {
                    options.AuthServer.ClientRegistration.Dcr.Enabled = true;
                    options.AuthServer.ClientRegistration.Cimd.Enabled = true;
                    options.AuthServer.SeedAuthPage(page =>
                    {
                        page.EnabledCredentialTypes = ["password", "email_otp", "magic_link"];
                        page.EnablePasswordSignup = true;
                    });
                    options.AuthServer.Mfa.Enabled = true;
                    options.AuthServer.Mfa.AllowUserSelfEnrollmentByDefault = true;
                    options.AuthServer.SeedGoogleConnection("google-client", "google-secret", $"{Origin}/sqlos/auth/oidc/callback");
                },
                configureServices: services =>
                {
                    services.RemoveAll<ISqlOSAuthEmailSender>();
                    services.RemoveAll<ISqlOSEmailSender>();
                    services.AddSingleton<ISqlOSAuthEmailSender>(emailSender);
                    services.AddSingleton<ISqlOSEmailSender>(emailSender);
                    services.AddSingleton<IHttpClientFactory>(new FakeCimdAndOidcHttpClientFactory(new Dictionary<string, string>
                    {
                        [CimdClientId] = JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["client_id"] = CimdClientId,
                            ["client_name"] = "Evil CIMD Client",
                            ["redirect_uris"] = new[] { AttackerRedirect },
                            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
                            ["response_types"] = new[] { "code" },
                            ["token_endpoint_auth_method"] = "none"
                        })
                    }));
                });
            return new DirectLoginHost(fixture, emailSender);
        }

        public async Task<ThirdPartyClient> RegisterThirdPartyClientAsync(ThirdPartyKind kind)
        {
            string clientId;
            switch (kind)
            {
                case ThirdPartyKind.Cimd:
                    // The attacker only hosts the metadata document; any route that names the client resolves it.
                    clientId = CimdClientId;
                    await using (var scope = Fixture.App.Services.CreateAsyncScope())
                    {
                        await scope.ServiceProvider.GetRequiredService<SqlOSClientResolutionService>()
                            .ResolveRequiredClientAsync(CimdClientId, AttackerRedirect);
                    }

                    break;
                case ThirdPartyKind.Dcr:
                    using (var response = await Client.PostAsJsonAsync("/sqlos/auth/register", new
                    {
                        client_name = "Evil DCR Client",
                        redirect_uris = new[] { AttackerRedirect },
                        grant_types = new[] { "authorization_code", "refresh_token" },
                        response_types = new[] { "code" },
                        token_endpoint_auth_method = "none"
                    }))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        response.StatusCode.Should().Be(HttpStatusCode.Created, "body: {0}", body);
                        using var json = JsonDocument.Parse(body);
                        clientId = json.RootElement.GetProperty("client_id").GetString()!;
                    }

                    break;
                default:
                    // The admin/dashboard default: IsFirstParty stays false unless an operator sets it.
                    clientId = AdminClientId;
                    await using (var scope = Fixture.App.Services.CreateAsyncScope())
                    {
                        await scope.ServiceProvider.GetRequiredService<SqlOSAdminService>().CreateClientAsync(
                            new SqlOSCreateClientRequest(AdminClientId, "Admin Third Party", "sqlos", [AttackerRedirect]));
                    }

                    break;
            }

            var client = await QueryAsync(db => db.Set<SqlOSClientApplication>()
                .AsNoTracking()
                .SingleAsync(x => x.ClientId == clientId));
            client.IsFirstParty.Should().BeFalse();
            return new ThirdPartyClient(client.Id, client.ClientId);
        }

        public async Task<TestUser> CreateUserAsync(string label, int organizationCount = 0, string? memberOf = null)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var email = $"{label}-{Guid.NewGuid():N}@example.test";
            var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest($"Direct Login {label}", email, Password));
            for (var index = 0; index < organizationCount; index++)
            {
                var organization = await admin.CreateOrganizationAsync(
                    new SqlOSCreateOrganizationRequest($"{label} org {index} {Guid.NewGuid():N}", null));
                await admin.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
            }

            if (memberOf != null)
            {
                await admin.CreateMembershipAsync(memberOf, new SqlOSCreateMembershipRequest(user.Id, "member"));
            }

            return new TestUser(user.Id, email);
        }

        public async Task<string> CreateOrganizationRequiringMfaAsync()
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            var organization = await scope.ServiceProvider.GetRequiredService<SqlOSAdminService>()
                .CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"MFA org {Guid.NewGuid():N}", null));
            await scope.ServiceProvider.GetRequiredService<SqlOSSettingsService>().UpdateOrganizationMfaPolicyAsync(
                organization.Id,
                new SqlOSUpdateOrganizationMfaPolicyRequest(
                    IsEnabled: true,
                    RequireMfaForAllUsers: true,
                    RequireMfaForOwnersAndAdmins: true,
                    UserSelfEnrollmentEnabled: true,
                    RecoveryCodesEnabled: true,
                    RequiredRoles: null,
                    AvailableFactors: null));
            return organization.Id;
        }

        public async Task<string> TotpCodeAsync(string secret, DateTimeOffset? at = null)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            return scope.ServiceProvider.GetRequiredService<SqlOSTotpMfaService>().GenerateCodeForTesting(secret, at);
        }

        public async Task<string> EnrollTotpAsync(string userId)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            var auth = scope.ServiceProvider.GetRequiredService<SqlOSAuthService>();
            var totp = scope.ServiceProvider.GetRequiredService<SqlOSTotpMfaService>();
            var enrollment = await auth.StartTotpEnrollmentAsync(userId, new SqlOSTotpEnrollmentStartRequest("Direct login authenticator"));
            await auth.VerifyTotpEnrollmentAsync(new SqlOSTotpEnrollmentVerifyRequest(
                enrollment.EnrollmentToken,
                totp.GenerateCodeForTesting(enrollment.Secret)));
            return enrollment.Secret;
        }

        public async Task<string> NextTotpCodeAsync(string secret)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            // The enrollment consumed the current time step; the next one is still inside the window.
            return scope.ServiceProvider.GetRequiredService<SqlOSTotpMfaService>()
                .GenerateCodeForTesting(secret, DateTimeOffset.UtcNow.AddSeconds(30));
        }

        public async Task<string> GetGoogleConnectionIdAsync()
            => await QueryAsync(db => db.Set<SqlOSOidcConnection>()
                .Where(x => x.ProviderType == SqlOSOidcProviderType.Google)
                .Select(x => x.Id)
                .SingleAsync());

        public async Task<Uri> StartSocialLoginAsync(string connectionId, string clientId, string codeVerifier, string redirectUri)
        {
            using var response = await Client.PostAsJsonAsync("/sqlos/auth/oidc/authorization-url", new
            {
                connectionId,
                clientId,
                redirectUri,
                state = $"state-{Guid.NewGuid():N}",
                codeChallenge = HostedAuthorizeTokenFixture.CreateCodeChallenge(codeVerifier),
                codeChallengeMethod = "S256"
            });
            return new Uri(await ReadStringAsync(response, "authorizationUrl"));
        }

        /// <summary>The victim's browser returning from the provider after a (silent) upstream sign-in.</summary>
        public async Task<HttpResponseMessage> CompleteProviderCallbackAsync(Uri providerUrl, string email)
        {
            var providerQuery = QueryHelpers.ParseQuery(providerUrl.Query);
            return await Client.GetAsync(QueryHelpers.AddQueryString(
                "/sqlos/auth/oidc/callback",
                new Dictionary<string, string?>
                {
                    ["state"] = providerQuery["state"].ToString(),
                    ["code"] = $"success:{email}:{providerQuery["nonce"]}"
                }));
        }

        public async Task<T> WhileFirstPartyAsync<T>(ThirdPartyClient client, Func<Task<T>> action)
        {
            await SetFirstPartyAsync(client, isFirstParty: true);
            try
            {
                return await action();
            }
            finally
            {
                await SetFirstPartyAsync(client, isFirstParty: false);
            }
        }

        public string LatestOtpCode(string email)
        {
            var message = EmailSender.Messages.Last(item => string.Equals(item.To, email, StringComparison.OrdinalIgnoreCase));
            return OtpCodePattern.Match(message.TextBody ?? string.Empty).Groups[1].Value;
        }

        public string LatestMagicLinkToken(string email)
        {
            var message = EmailSender.Messages.Last(item => string.Equals(item.To, email, StringComparison.OrdinalIgnoreCase));
            var match = MagicLinkTokenPattern.Match(message.TextBody ?? string.Empty);
            return Uri.UnescapeDataString(match.Groups[1].Value.TrimEnd('.', ')'));
        }

        public async Task<string?> FindUserIdAsync(string email)
        {
            var normalizedEmail = SqlOSAdminService.NormalizeEmail(email);
            return await QueryAsync(db => db.Set<SqlOSUserEmail>()
                .Where(x => x.NormalizedEmail == normalizedEmail)
                .Select(x => (string?)x.UserId)
                .SingleOrDefaultAsync());
        }

        public async Task<IReadOnlyList<Rejection>> ListRejectionsAsync(ThirdPartyClient client)
        {
            var events = await QueryAsync(db => db.Set<SqlOSAuditEvent>()
                .AsNoTracking()
                .Where(x => x.EventType == RejectedEvent)
                .Select(x => new { x.Id, x.MetadataJson })
                .ToListAsync());
            return events
                .Select(item =>
                {
                    using var metadata = JsonDocument.Parse(item.MetadataJson ?? "{}");
                    return new Rejection(
                        item.Id,
                        ReadString(metadata.RootElement, "client_id"),
                        ReadString(metadata.RootElement, "route"));
                })
                .Where(item => string.Equals(item.ClientId, client.ClientId, StringComparison.Ordinal))
                .ToList();
        }

        public async Task<IReadOnlyCollection<string>> ListRejectionIdsAsync(ThirdPartyClient client)
            => (await ListRejectionsAsync(client)).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

        public async Task<T> QueryAsync<T>(Func<TestSqlOSDbContext, Task<T>> query)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            return await query(scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>());
        }

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();

        private async Task SetFirstPartyAsync(ThirdPartyClient client, bool isFirstParty)
            => await QueryAsync(db => db.Set<SqlOSClientApplication>()
                .Where(x => x.Id == client.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IsFirstParty, isFirstParty)));

        private static string? ReadString(JsonElement element, string property)
            => element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
    }
}
