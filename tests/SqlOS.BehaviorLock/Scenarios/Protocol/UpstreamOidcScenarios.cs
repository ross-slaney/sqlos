using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Protocol.ProtocolForms;

namespace SqlOS.BehaviorLock.Scenarios.Protocol;

/// <summary>
/// Sign-in through upstream social and custom OpenID Connect providers: the provider list, the
/// first-party direct API (<c>/oidc/authorization-url</c>, <c>/oidc/callback</c>,
/// <c>/oidc/exchange</c>), the hosted provider buttons (<c>/login/oidc/{connectionId}</c>), the
/// <c>/continue</c> interstitial, and account linking. The fake upstream answers authorization
/// codes of the form <c>mode:email:nonce</c> (see <c>FakeOidcUpstream</c>).
/// </summary>
[TestClass]
public sealed class UpstreamOidcScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/oidc/providers")]
    public async Task The_provider_list_names_each_enabled_connection()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);

        t.Observe(await t.NewClient("app").GetAsync("/sqlos/auth/oidc/providers"), "enabled connections, ordered by display name");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/oidc/authorization-url")]
    [Covers("GET /sqlos/auth/oidc/callback")]
    [Covers("POST /sqlos/auth/oidc/exchange")]
    [Covers("GET /api/me")]
    public async Task A_first_party_app_signs_a_user_in_with_google_through_the_direct_api()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var app = t.NewClient("app-backend");
        var email = t.Unique.Email("grace");
        var google = await ConnectionIdAsync(t, "Google");

        var direct = NewDirectLogin(t);
        var started = await t.ObserveWithAuditAsync(
            await app.PostJsonAsync("/sqlos/auth/oidc/authorization-url", direct.Body(google, email)),
            "the app asks SqlOS for Google's authorization URL");
        var upstream = UpstreamParameters(started.JsonString("authorizationUrl"));
        var callback = await t.ObserveWithAuditAsync(
            await t.GetAsync(CallbackUrl($"success:{email}:{upstream["nonce"]}", upstream["state"])),
            "Google redirects back: SqlOS provisions Grace and returns a one-time code to the app's redirect URI");
        var exchanged = await t.ObserveWithAuditAsync(
            await app.PostJsonAsync("/sqlos/auth/oidc/exchange", direct.Exchange(callback.NextUrlParameter("code"))),
            "the app exchanges the code with its PKCE verifier for tokens");
        t.Observe(
            await app.GetAsync("/api/me", options => options.Bearer(exchanged.JsonString("tokens.accessToken"))),
            "the access token opens the API");
        await t.ObserveUnhandledAsync(
            async () => await app.PostJsonAsync("/sqlos/auth/oidc/exchange", direct.Exchange(callback.NextUrlParameter("code"))),
            "exchanging the same code again");

        await t.ObserveStateAsync("/sqlos/admin/auth/api/users?search=grace", "Grace was provisioned");
        await t.ApproveAsync();
    }

    [Scenario]
    public async Task Invalid_direct_social_sign_in_requests_fail_unhandled()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var app = t.NewClient("app-backend");
        var google = await ConnectionIdAsync(t, "Google");
        var direct = NewDirectLogin(t);
        Dictionary<string, object?> Body(params (string Name, object? Value)[] changes)
        {
            var body = direct.Body(google, email: null);
            foreach (var (name, value) in changes)
            {
                body[name] = value;
            }

            return body;
        }

        await t.ObserveUnhandledAsync(async () => await app.PostJsonAsync("/sqlos/auth/oidc/authorization-url", Body(("codeChallenge", ""))), "no PKCE challenge");
        await t.ObserveUnhandledAsync(async () => await app.PostJsonAsync("/sqlos/auth/oidc/authorization-url", Body(("codeChallengeMethod", "plain"))), "the plain PKCE method");
        await t.ObserveUnhandledAsync(async () => await app.PostJsonAsync("/sqlos/auth/oidc/authorization-url", Body(("codeChallenge", "too-short"))), "a challenge that is not 43 characters");
        await t.ObserveUnhandledAsync(async () => await app.PostJsonAsync("/sqlos/auth/oidc/authorization-url", Body(("clientId", "unknown-client"))), "an unknown client");
        await t.ObserveUnhandledAsync(
            async () => await app.PostJsonAsync("/sqlos/auth/oidc/authorization-url", Body(("redirectUri", "https://attacker.example/callback"))),
            "a redirect URI the client did not register");
        await t.ObserveUnhandledAsync(async () => await app.PostJsonAsync("/sqlos/auth/oidc/authorization-url", Body(("connectionId", "oidc_00000000000000000000000000000000"))), "an unknown connection");
        await t.ObserveAuditAsync("the unknown connection wrote a start error");
        await t.ObserveUnhandledAsync(async () => await app.PostJsonAsync("/sqlos/auth/oidc/exchange", direct.Exchange("not-a-code")), "exchanging an unknown code");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/oidc/callback")]
    [Covers("POST /sqlos/auth/oidc/callback")]
    public async Task The_callback_needs_the_one_time_state_and_accepts_a_form_post()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var app = t.NewClient("app-backend");
        var email = t.Unique.Email("grace");
        var google = await ConnectionIdAsync(t, "Google");

        t.Observe(await t.GetAsync("/sqlos/auth/oidc/callback?code=anything"), "no state");
        t.Observe(await t.GetAsync(CallbackUrl("anything", "not-a-provider-state")), "an unknown state");

        var direct = NewDirectLogin(t);
        var upstream = UpstreamParameters(t.Discard(await app.PostJsonAsync("/sqlos/auth/oidc/authorization-url", direct.Body(google, email))).JsonString("authorizationUrl"));
        await t.SkipAuditAsync();
        await t.ObserveWithAuditAsync(
            await t.PostFormAsync("/sqlos/auth/oidc/callback", Form(("code", $"success:{email}:{upstream["nonce"]}"), ("state", upstream["state"])), options => options.WithOrigin("https://accounts.google.com")),
            "the provider posts the response as a form (response_mode=form_post)");
        t.Observe(await t.GetAsync(CallbackUrl($"success:{email}:{upstream["nonce"]}", upstream["state"])), "the state was consumed: replaying the callback");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/oidc/callback")]
    public async Task Provider_errors_and_failed_upstream_checks_return_to_the_app_with_an_error()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var app = t.NewClient("app-backend");
        var email = t.Unique.Email("grace");
        var google = await ConnectionIdAsync(t, "Google");
        var custom = await ConnectionIdAsync(t, "Example OIDC");

        async Task CallbackAsync(string connectionId, Func<IReadOnlyDictionary<string, string>, string> query, string caption)
        {
            var direct = NewDirectLogin(t);
            var upstream = UpstreamParameters(t.Discard(await app.PostJsonAsync("/sqlos/auth/oidc/authorization-url", direct.Body(connectionId, email))).JsonString("authorizationUrl"));
            await t.SkipAuditAsync();
            await t.ObserveWithAuditAsync(await t.GetAsync("/sqlos/auth/oidc/callback?" + query(upstream)), caption);
        }

        string State(IReadOnlyDictionary<string, string> upstream) => "state=" + Uri.EscapeDataString(upstream["state"]);
        string Code(string code) => "code=" + Uri.EscapeDataString(code);

        await CallbackAsync(google, upstream => $"error=access_denied&error_description=User%20cancelled&{State(upstream)}", "the provider reports an error");
        await CallbackAsync(google, upstream => State(upstream), "no code");
        await CallbackAsync(google, upstream => $"{Code("bad-code")}&{State(upstream)}", "the provider's token endpoint refuses the code");
        await CallbackAsync(google, upstream => $"{Code($"success:{email}:another-nonce")}&{State(upstream)}", "the ID token carries another nonce");
        await CallbackAsync(google, upstream => $"{Code($"userinfo-sub-mismatch:{email}:{upstream["nonce"]}")}&{State(upstream)}", "UserInfo names another subject than the ID token");
        await CallbackAsync(google, upstream => $"{Code($"unverified:{email}:{upstream["nonce"]}")}&{State(upstream)}", "Google says the email is not verified");
        await CallbackAsync(google, upstream => $"{Code($"success::{upstream["nonce"]}")}&{State(upstream)}", "no email address");
        await CallbackAsync(custom, upstream => $"{Code($"success:{email}:{upstream["nonce"]}")}&{State(upstream)}", "a successful custom-provider callback for comparison");

        var tampered = NewDirectLogin(t);
        var tamperedUpstream = UpstreamParameters(t.Discard(await app.PostJsonAsync("/sqlos/auth/oidc/authorization-url", tampered.Body(google, email))).JsonString("authorizationUrl"));
        await t.SkipAuditAsync();
        await t.ObserveUnhandledAsync(
            async () => await t.GetAsync(CallbackUrl($"tampered-amr:{email}:{tamperedUpstream["nonce"]}", tamperedUpstream["state"])),
            "an ID token whose payload was changed after signing");
        await t.ObserveAuditAsync("the failed signature check");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login/oidc/{connectionId}")]
    [Covers("GET /sqlos/auth/oidc/callback")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Each_provider_type_signs_a_user_in_from_the_hosted_page()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);

        foreach (var (provider, name) in new[] { ("Example OIDC", "carol"), ("Microsoft", "dave"), ("GitHub", "erin") })
        {
            var email = t.Unique.Email(name);
            var browser = t.NewBrowser($"{name}-browser");
            var request = t.Urls.Authorize();
            var page = t.Discard(await browser.GetAsync(request.Url));
            var toProvider = t.Observe(await browser.GetAsync(ProviderLink(page, provider)), $"{provider}: the hosted page's button redirects to the provider");
            var state = toProvider.NextUrlParameter("state");
            var nonce = toProvider.NextUrlParameters.TryGetValue("nonce", out var value) ? value : "no-nonce";
            var callback = t.Observe(await browser.GetAsync(CallbackUrl($"success:{email}:{nonce}", state)), $"{provider}: the provider redirects back and SqlOS returns a code to the client");
            t.ObserveTokens(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(callback.NextUrlParameter("code"))), $"{provider}: the code redeems");
        }

        await t.ObserveAuditAsync("provider sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login/oidc/{connectionId}")]
    public async Task The_hosted_provider_route_needs_an_active_authorization_request_and_a_known_connection()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var google = await ConnectionIdAsync(t, "Google");
        var page = t.Discard(await t.GetAsync(t.Urls.Authorize().Url));
        var requestId = page.Form("/login/identify")["requestId"];

        t.Observe(await t.GetAsync($"/sqlos/auth/login/oidc/{google}"), "no authorization request");
        await t.ObserveUnhandledAsync(async () => await t.GetAsync($"/sqlos/auth/login/oidc/{google}?request=req_00000000000000000000000000000000"), "an unknown authorization request");
        await t.ObserveUnhandledAsync(async () => await t.GetAsync($"/sqlos/auth/login/oidc/oidc_00000000000000000000000000000000?request={requestId}"), "an unknown connection");

        await t.ObserveAuditAsync("the unknown connection wrote a start error");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/oidc/authorization-url")]
    [Covers("GET /sqlos/auth/oidc/providers")]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("GET /sqlos/auth/login/oidc/{connectionId}")]
    [Covers("GET /sqlos/auth/oidc/callback")]
    [Covers("GET /sqlos/auth/continue")]
    [Covers("POST /sqlos/auth/consent/approve")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_third_party_client_cannot_use_direct_social_login_but_signs_in_through_authorize_and_consent()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Dcr);
        await t.Setup.OperatorPostAsync("/sqlos/admin/auth/api/oidc-connections", new
        {
            providerType = "Google",
            displayName = "Google",
            clientId = "google-client-id",
            clientSecret = "google-client-secret",
            allowedCallbackUris = new[] { BehaviorLockConstants.SocialCallbackUri },
            useDiscovery = true
        });
        var registeredClient = await RegisterPublicClientAsync(t, "Taskrail Desktop", "http://127.0.0.1/callback/taskrail");
        var cimdClient = CimdClientId("taskrail-web");
        var cimdRedirect = CimdRedirectUri("taskrail-web");
        t.Setup.PublishClientMetadata(cimdClient, ClientMetadataDocument(cimdClient, "Taskrail Web", cimdRedirect));
        var email = t.Unique.Email("grace");
        var attacker = t.NewClient("third-party-backend");

        var providers = t.Observe(await attacker.GetAsync("/sqlos/auth/oidc/providers"), "the operator added Google");
        var google = providers.JsonString("0.connectionId");
        var direct = NewDirectLogin(t);
        await t.ObserveWithAuditAsync(
            await attacker.PostJsonAsync("/sqlos/auth/oidc/authorization-url", direct.Body(google, email, registeredClient, "http://127.0.0.1/callback/taskrail")),
            "a dynamically registered client asks for a direct Google sign-in: refused, nothing is minted");
        await t.ObserveWithAuditAsync(
            await attacker.PostJsonAsync("/sqlos/auth/oidc/authorization-url", direct.Body(google, email, cimdClient, cimdRedirect)),
            "so is a client ID metadata document client");

        var request = t.Urls.Authorize(cimdClient, cimdRedirect);
        var page = t.Observe(await t.GetAsync(request.Url), "the metadata-document client uses /authorize instead: the sign-in page offers Google");
        var toGoogle = t.Observe(await t.GetAsync(ProviderLink(page, "Google")), "Grace chooses Google");
        var callback = await t.ObserveWithAuditAsync(
            await t.GetAsync(CallbackUrl($"success:{email}:{toGoogle.NextUrlParameter("nonce")}", toGoogle.NextUrlParameter("state"))),
            "Google returns: consent is still required, so SqlOS continues through /continue with a continuation cookie");
        var consent = t.Observe(await t.GetAsync(callback.Location!), "the continuation shows the consent page");
        t.Observe(await t.NewBrowser("other-browser").GetAsync(callback.Location!), "the same continuation URL without the cookie");
        var approved = await t.ObserveWithAuditAsync(await t.SubmitAsync(consent.Form("/consent/approve")), "Grace approves");
        t.ObserveTokens(
            await t.NewClient("taskrail-web").PostFormAsync("/sqlos/auth/token", request.TokenRequest(approved.NextUrlParameter("code"))),
            "the client redeems the code");
        t.Observe(await t.GetAsync(callback.Location!), "reloading the continuation after the request completed");

        await t.ObserveAuditAsync("remaining events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/continue")]
    public async Task A_continuation_cookie_only_continues_its_own_authorization_request()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Dcr);
        await t.Setup.OperatorPostAsync("/sqlos/admin/auth/api/oidc-connections", new
        {
            providerType = "Google",
            displayName = "Google",
            clientId = "google-client-id",
            clientSecret = "google-client-secret",
            allowedCallbackUris = new[] { BehaviorLockConstants.SocialCallbackUri },
            useDiscovery = true
        });
        var clientId = CimdClientId("taskrail-web");
        var redirectUri = CimdRedirectUri("taskrail-web");
        t.Setup.PublishClientMetadata(clientId, ClientMetadataDocument(clientId, "Taskrail Web", redirectUri));

        async Task<HttpExchange> ReachContinuationAsync(HttpActor browser, string email)
        {
            var page = t.Discard(await browser.GetAsync(t.Urls.Authorize(clientId, redirectUri).Url));
            var toGoogle = t.Discard(await browser.GetAsync(ProviderLink(page, "Google")));
            var callback = t.Discard(await browser.GetAsync(CallbackUrl($"success:{email}:{toGoogle.NextUrlParameter("nonce")}", toGoogle.NextUrlParameter("state"))));
            await t.SkipAuditAsync();
            return callback;
        }

        var first = await ReachContinuationAsync(t.NewBrowser("grace-browser"), t.Unique.Email("grace"));
        var second = await ReachContinuationAsync(t.NewBrowser("heidi-browser"), t.Unique.Email("heidi"));
        var firstCookie = first.SetCookies.Single(header => header.StartsWith("sqlos_auth_continue_", StringComparison.Ordinal)).Split(';')[0].Split('=', 2);
        var secondCookieName = second.SetCookies.Single(header => header.StartsWith("sqlos_auth_continue_", StringComparison.Ordinal)).Split('=', 2)[0];
        t.Note("Grace and Heidi each signed in with Google for the metadata-document client and hold a continuation for their own request.");

        var swapped = t.NewBrowser("swapping-browser");
        swapped.SetCookie(secondCookieName, firstCookie[1]);
        await t.ObserveWithAuditAsync(await swapped.GetAsync(second.Location!), "Grace's continuation handle presented for Heidi's request");
        await t.ObserveWithAuditAsync(await t.NewBrowser("no-cookie").GetAsync("/sqlos/auth/continue"), "no request at all");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/oidc/callback")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /sqlos/auth/userinfo")]
    public async Task A_verified_social_sign_in_claims_an_unverified_account_and_revokes_the_pre_registered_password()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var victim = await t.Setup.CreateUserAsync("victim");
        var attackerBrowser = t.NewBrowser("attacker-browser");
        var attackerSession = await t.Setup.SignInWithPasswordAsync(victim, browser: attackerBrowser);
        t.Note("An attacker registered the victim's address with a password of their choosing; the address is unverified and the attacker is signed in.");

        var request = t.Urls.Authorize();
        var page = t.Discard(await t.GetAsync(request.Url));
        var toGoogle = t.Discard(await t.GetAsync(ProviderLink(page, "Google")));
        await t.SkipAuditAsync();
        var callback = await t.ObserveWithAuditAsync(
            await t.GetAsync(CallbackUrl($"success:{victim.Email}:{toGoogle.NextUrlParameter("nonce")}", toGoogle.NextUrlParameter("state"))),
            "the real owner signs in with Google, which verified the address: SqlOS claims the account for her");
        var tokens = t.ObserveTokens(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(callback.NextUrlParameter("code"))), "the owner's code redeems");
        t.Observe(await t.Api.GetAsync("/sqlos/auth/userinfo", options => options.Bearer(tokens.JsonString("access_token"))), "the claimed address is verified now");

        t.ObserveTokens(
            await t.Api.PostFormAsync("/sqlos/auth/token", Refresh(BehaviorLockConstants.AppClientId, attackerSession.RefreshToken)),
            "the attacker's refresh token was revoked");
        var attackerAgain = t.NewBrowser("attacker-again");
        var passwordPage = t.Discard(await attackerAgain.GetAsync(t.Urls.Authorize(extra: new Dictionary<string, string?> { ["view"] = "password" }).Url));
        await t.ObserveWithAuditAsync(
            await attackerAgain.SubmitAsync(passwordPage.Form("/login/password").With("email", victim.Email).With("password", victim.Password)),
            "and the attacker's password no longer signs in");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/oidc/callback")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_look_alike_email_from_a_provider_never_links_to_the_real_account()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var domain = t.Unique.Domain("business");
        var realEmail = $"bob@{domain}";
        t.Scrub(realEmail, "email", "bob-real");
        t.Scrub(realEmail.ToUpperInvariant(), "email", "BOB-REAL");
        await t.Setup.OperatorPostAsync("/sqlos/admin/auth/api/users", new { displayName = "Bob Real", email = realEmail, password = t.Unique.Password("bob-real") });
        var lookAlikeDomain = domain.Replace("ss", "\u00DF", StringComparison.Ordinal);
        var lookAlike = $"bob@{lookAlikeDomain}";
        t.Scrub(lookAlike, "email", "bob-look-alike");
        t.Scrub(lookAlike.ToUpperInvariant(), "email", "BOB-LOOK-ALIKE");
        var asciiLookAlike = $"bob@{new IdnMapping().GetAscii(lookAlikeDomain)}";
        t.Scrub(asciiLookAlike, "email", "bob-look-alike-ascii");
        t.Scrub(asciiLookAlike.ToUpperInvariant(), "email", "BOB-LOOK-ALIKE-ASCII");
        t.Scrub(new IdnMapping().GetAscii(lookAlikeDomain), "domain", "business-look-alike-ascii");
        t.Note("Bob's account is at a domain containing \"ss\"; an attacker controls the same domain with \"ß\" (issue #422).");

        var request = t.Urls.Authorize();
        var page = t.Discard(await t.GetAsync(request.Url));
        var toGoogle = t.Discard(await t.GetAsync(ProviderLink(page, "Google")));
        await t.SkipAuditAsync();
        var callback = await t.ObserveWithAuditAsync(
            await t.GetAsync(CallbackUrl($"success:{lookAlike}:{toGoogle.NextUrlParameter("nonce")}", toGoogle.NextUrlParameter("state"))),
            "Google asserts the verified look-alike address");
        t.ObserveTokens(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(callback.NextUrlParameter("code"))), "the sign-in completes as a separate account, not Bob's");

        var zeroWidthBrowser = t.NewBrowser("zero-width");
        var zeroWidthPage = t.Discard(await zeroWidthBrowser.GetAsync(t.Urls.Authorize().Url));
        var zeroWidthGoogle = t.Discard(await zeroWidthBrowser.GetAsync(ProviderLink(zeroWidthPage, "Google")));
        await t.SkipAuditAsync();
        t.Scrub("bob\u200B@" + domain, "email", "bob-zero-width");
        await t.ObserveWithAuditAsync(
            await zeroWidthBrowser.GetAsync(CallbackUrl($"success:bob\u200B@{domain}:{zeroWidthGoogle.NextUrlParameter("nonce")}", zeroWidthGoogle.NextUrlParameter("state"))),
            "an address with a zero-width character is not usable");

        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users?search=bob", "the users named Bob");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/oidc/callback")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_custom_provider_provisions_an_unverified_user_and_audits_it_after_the_save_CurrentBehavior_KnownDefect_415()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var carol = t.Unique.Email("carol");

        var request = t.Urls.Authorize();
        var page = t.Discard(await t.GetAsync(request.Url));
        var toProvider = t.Discard(await t.GetAsync(ProviderLink(page, "Example OIDC")));
        await t.SkipAuditAsync();
        var callback = t.Observe(
            await t.GetAsync(CallbackUrl($"unverified:{carol}:{toProvider.NextUrlParameter("nonce")}", toProvider.NextUrlParameter("state"))),
            "a custom provider need not verify the email: SqlOS provisions Carol with an unverified address");
        t.ObserveTokens(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(callback.NextUrlParameter("code"))), "the code redeems");
        await t.ObserveAuditAsync(
            "known defect #415: provisioning writes user.login.oidc.provisioned after the user is saved, without the request's address, and no user.created");

        var aliceBrowser = t.NewBrowser("alice-browser");
        var secondPage = t.Discard(await aliceBrowser.GetAsync(t.Urls.Authorize().Url));
        var secondProvider = t.Discard(await aliceBrowser.GetAsync(ProviderLink(secondPage, "Example OIDC")));
        await t.SkipAuditAsync();
        await t.ObserveWithAuditAsync(
            await aliceBrowser.GetAsync(CallbackUrl($"unverified:{alice.Email}:{secondProvider.NextUrlParameter("nonce")}", secondProvider.NextUrlParameter("state"))),
            "an unverified upstream address that already has an account is never linked");

        await t.ApproveAsync();
    }

    private static async Task<string> ConnectionIdAsync(Transcript t, string displayName)
    {
        var providers = t.Discard(await t.NewClient("provider-lookup").GetAsync("/sqlos/auth/oidc/providers"));
        return providers.Json!.AsArray()
            .Single(provider => provider!["displayName"]!.GetValue<string>() == displayName)!["connectionId"]!.GetValue<string>();
    }

    /// <summary>The <c>/login/oidc/{connectionId}</c> link of a hosted page's provider button.</summary>
    private static string ProviderLink(HttpExchange page, string displayName)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(page.ResponseBody);
        return document.QuerySelectorAll("a.provider-link")
            .Single(link => link.GetAttribute("data-loading-label") == $"Connecting to {displayName}")
            .GetAttribute("href")!;
    }

    private static string CallbackUrl(string code, string state)
        => $"/sqlos/auth/oidc/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}";

    private static IReadOnlyDictionary<string, string> UpstreamParameters(string authorizationUrl)
        => QueryHelpers.ParseQuery(new Uri(authorizationUrl).Query).ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);

    private static DirectLogin NewDirectLogin(Transcript t)
    {
        var generated = t.Urls.Authorize();
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(generated.CodeVerifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new DirectLogin(generated.State, generated.CodeVerifier, challenge);
    }

    /// <summary>A PKCE pair and state the app keeps for one direct social sign-in.</summary>
    private sealed record DirectLogin(string State, string CodeVerifier, string CodeChallenge)
    {
        public Dictionary<string, object?> Body(
            string connectionId,
            string? email,
            string clientId = BehaviorLockConstants.AppClientId,
            string redirectUri = BehaviorLockConstants.AppRedirectUri)
            => new()
            {
                ["connectionId"] = connectionId,
                ["clientId"] = clientId,
                ["redirectUri"] = redirectUri,
                ["state"] = State,
                ["codeChallenge"] = CodeChallenge,
                ["codeChallengeMethod"] = "S256",
                ["email"] = email
            };

        public object Exchange(string code, string clientId = BehaviorLockConstants.AppClientId, string redirectUri = BehaviorLockConstants.AppRedirectUri)
            => new { code, clientId, redirectUri, codeVerifier = CodeVerifier };
    }
}
