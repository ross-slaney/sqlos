using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Calendar.Models;
using SqlOS.Calendar.Services;
using SqlOS.Domain;
using static SqlOS.Tests.Domain.DomainTime;
using Kinds = SqlOS.AuthServer.Models.SqlOSTemporaryTokenKinds;

namespace SqlOS.Tests.Domain;

/// <summary>
/// The catalog of SqlOS's temporary token kinds holds 7.2.1's purposes, lifetimes and payload
/// formats exactly: a token minted by 7.2.1 still reads, and a token minted now is stored as 7.2.1
/// stored it.
/// </summary>
[TestClass]
public sealed class SqlOSTemporaryTokenKindsTests
{
    [TestMethod]
    public void Every_kind_keeps_its_7_2_1_purpose_and_the_purposes_are_distinct()
    {
        var purposes = Kinds.All.ToDictionary(kind => kind.Purpose, kind => kind);

        purposes.Keys.Should().BeEquivalentTo(
        [
            "password_reset",
            "password_reset_request",
            "email_verification",
            "mfa_challenge",
            "mfa_totp_enrollment",
            "pending_auth",
            "auth_page_pending",
            "auth_page_consent",
            "authorization_continue",
            "auth.magic_link",
            "email_otp_signup",
            "phone_otp_signup",
            "oidc_browser_request",
            "oidc_authorization_request",
            "oidc_browser_code",
            "calendar_connect_request",
            "auth_page_session"
        ]);
        Kinds.All.Should().OnlyHaveUniqueItems(kind => kind.Purpose);
        Kinds.Purposes.LegacySamlAuthorizationCode.Should().Be("auth_code");
    }

    [TestMethod]
    public void The_catalog_lists_every_kind_it_defines()
    {
        var defined = typeof(Kinds).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => typeof(TemporaryTokenKind).IsAssignableFrom(property.PropertyType))
            .Select(property => (TemporaryTokenKind)property.GetValue(null)!)
            .ToList();

        Kinds.All.Should().BeEquivalentTo(defined);
    }

    [TestMethod]
    public void The_public_purpose_constants_keep_their_values()
    {
        SqlOSAuthService.MfaChallengePurpose.Should().Be(Kinds.MfaChallenge.Purpose).And.Be("mfa_challenge");
        SqlOSTotpMfaService.EnrollmentPurpose.Should().Be(Kinds.TotpEnrollment.Purpose).And.Be("mfa_totp_enrollment");
        SqlOSMagicLinkService.TokenPurpose.Should().Be(Kinds.MagicLink.Purpose).And.Be("auth.magic_link");
    }

    [TestMethod]
    public void Fixed_lifetimes_are_the_7_2_1_constants_and_the_rest_are_configured()
    {
        Kinds.EmailVerification.Lifetime.FixedLifetime.Should().Be(TimeSpan.FromDays(1));
        Kinds.AuthPagePending.Lifetime.FixedLifetime.Should().Be(TimeSpan.FromMinutes(10));
        Kinds.AuthPageConsent.Lifetime.FixedLifetime.Should().Be(TimeSpan.FromMinutes(10));
        Kinds.OidcBrowserCode.Lifetime.FixedLifetime.Should().Be(TimeSpan.FromMinutes(5));
        Kinds.All.Except(new TemporaryTokenKind[] { Kinds.EmailVerification, Kinds.AuthPagePending, Kinds.AuthPageConsent, Kinds.OidcBrowserCode })
            .Should().OnlyContain(kind => kind.Lifetime.IsConfigured && !string.IsNullOrWhiteSpace(kind.Lifetime.Setting));
    }

    [TestMethod]
    public void Only_issuer_sessions_and_reset_request_records_are_not_spent_by_use()
        => Kinds.All.Where(kind => !kind.IsSingleUse).Should().BeEquivalentTo(new TemporaryTokenKind[] { Kinds.IssuerSession, Kinds.PasswordResetRequest });

    [TestMethod]
    public void A_host_kind_keeps_the_7x_api_any_binding_any_payload_and_a_configured_lifetime()
    {
        var host = Kinds.ForHost("oidc_handoff");

        host.Purpose.Should().Be("oidc_handoff");
        host.Bindings.Should().Be(TemporaryTokenBindings.All);
        host.IsSingleUse.Should().BeTrue();
        host.Lifetime.IsConfigured.Should().BeTrue();
        host.Serialize(new { UserId = "usr_1" }).Should().Be("{\"UserId\":\"usr_1\"}");
    }

    [TestMethod]
    public void Payloads_are_stored_byte_for_byte_as_7_2_1_stored_them()
    {
        AssertStored(Kinds.PasswordReset, new PasswordResetPayload("eml_1", "ALICE@EXAMPLE.TEST"),
            "{\"EmailId\":\"eml_1\",\"NormalizedEmail\":\"ALICE@EXAMPLE.TEST\"}");
        AssertStored(Kinds.PasswordResetRequest, new PasswordResetRequestPayload("ALICE@EXAMPLE.TEST", "203.0.113.10", "app", "public_api"),
            "{\"NormalizedEmail\":\"ALICE@EXAMPLE.TEST\",\"IpAddress\":\"203.0.113.10\",\"ClientKey\":\"app\",\"Surface\":\"public_api\"}");
        AssertStored(Kinds.EmailVerification, new EmailVerificationPayload("eml_1"),
            "{\"EmailId\":\"eml_1\"}");
        AssertStored(Kinds.MfaChallenge, new SqlOSMfaChallengePayload("client", "app", "password", "req_1", "https://api.example.test", true, ["totp"], 2, true),
            "{\"Flow\":\"client\",\"ClientId\":\"app\",\"AuthenticationMethod\":\"password\",\"AuthorizationRequestId\":\"req_1\",\"Resource\":\"https://api.example.test\",\"EnrollmentRequired\":true,\"PermittedEnrollmentFactors\":[\"totp\"],\"FailedAttempts\":2,\"CredentialSignIn\":true}");
        AssertStored(Kinds.TotpEnrollment, new TotpEnrollmentPayload("aut_1", new TotpEnrollmentChallengeBinding("tmp_1", "usr_1", "cli_1", null, "client", "app", null, null)),
            "{\"AuthenticatorId\":\"aut_1\",\"ChallengeBinding\":{\"ChallengeTokenId\":\"tmp_1\",\"UserId\":\"usr_1\",\"ClientApplicationId\":\"cli_1\",\"OrganizationId\":null,\"Flow\":\"client\",\"ClientId\":\"app\",\"AuthorizationRequestId\":null,\"Resource\":null}}");
        AssertStored(Kinds.PendingAuth, new PendingAuthPayload("app", "password"),
            "{\"ClientId\":\"app\",\"AuthenticationMethod\":\"password\"}");
        AssertStored(Kinds.AuthPagePending, new PendingAuthorizationPayload("req_1", "password", Now, true),
            "{\"AuthorizationRequestId\":\"req_1\",\"AuthenticationMethod\":\"password\",\"AuthenticatedAt\":\"2026-09-30T12:00:00Z\",\"CredentialSignIn\":true}");
        AssertStored(Kinds.AuthPageConsent, new PendingConsentPayload("req_1", "password", Now, "fp", false),
            "{\"AuthorizationRequestId\":\"req_1\",\"AuthenticationMethod\":\"password\",\"AuthenticatedAt\":\"2026-09-30T12:00:00Z\",\"ClientMetadataFingerprint\":\"fp\",\"CredentialSignIn\":false}");
        AssertStored(Kinds.AuthorizationContinuation, new AuthorizationContinuationPayload("req_1", "mfa", "pending"),
            "{\"AuthorizationRequestId\":\"req_1\",\"MfaToken\":\"mfa\",\"PendingToken\":\"pending\",\"ConsentToken\":null}");
        AssertStored(Kinds.MagicLink, new MagicLinkPayload("alice@example.test", "ALICE@EXAMPLE.TEST", "al***@example.test", "eml_1", "req_1", "cli_1", null, "203.0.113.10", "UA", true),
            "{\"Email\":\"alice@example.test\",\"NormalizedEmail\":\"ALICE@EXAMPLE.TEST\",\"MaskedEmail\":\"al***@example.test\",\"UserEmailId\":\"eml_1\",\"AuthorizationRequestId\":\"req_1\",\"ClientApplicationId\":\"cli_1\",\"RequestedOrganizationId\":null,\"IpAddress\":\"203.0.113.10\",\"UserAgent\":\"UA\",\"Sent\":true}");
        AssertStored(Kinds.EmailOtpSignup, new EmailOtpSignupPayload("HASH", "req_1", "app", "cli_1", "Alice", "alice@example.test", "Acme", null, JsonNode.Parse("{\"plan\":\"pro\"}")!.AsObject()),
            "{\"ChallengeTokenHash\":\"HASH\",\"AuthorizationRequestId\":\"req_1\",\"ClientId\":\"app\",\"ClientApplicationId\":\"cli_1\",\"DisplayName\":\"Alice\",\"Email\":\"alice@example.test\",\"OrganizationName\":\"Acme\",\"OrganizationId\":null,\"CustomFields\":{\"plan\":\"pro\"}}");
        AssertStored(Kinds.PhoneOtpSignup, new PhoneOtpSignupPayload("HASH", null, "app", "cli_1", "Bob", "+16502530000", null, "org_1", null),
            "{\"ChallengeTokenHash\":\"HASH\",\"AuthorizationRequestId\":null,\"ClientId\":\"app\",\"ClientApplicationId\":\"cli_1\",\"DisplayName\":\"Bob\",\"PhoneNumber\":\"\\u002B16502530000\",\"OrganizationName\":null,\"OrganizationId\":\"org_1\",\"CustomFields\":null}");
        AssertStored(Kinds.OidcBrowserRequest, new OidcBrowserRequestPayload("app", "https://app.example.test/cb", "state", "challenge", "S256", "oidc_1", null, "nonce", "verifier", "https://sqlos.example.test/cb"),
            "{\"ClientId\":\"app\",\"RedirectUri\":\"https://app.example.test/cb\",\"State\":\"state\",\"CodeChallenge\":\"challenge\",\"CodeChallengeMethod\":\"S256\",\"ConnectionId\":\"oidc_1\",\"Email\":null,\"ProviderNonce\":\"nonce\",\"ProviderCodeVerifier\":\"verifier\",\"CallbackUri\":\"https://sqlos.example.test/cb\"}");
        AssertStored(Kinds.OidcAuthorizationRequest, new OidcAuthorizationRequestPayload("req_1", "oidc_1", "nonce", "verifier", "https://sqlos.example.test/cb", "alice@example.test"),
            "{\"AuthorizationRequestId\":\"req_1\",\"ConnectionId\":\"oidc_1\",\"ProviderNonce\":\"nonce\",\"ProviderCodeVerifier\":\"verifier\",\"CallbackUri\":\"https://sqlos.example.test/cb\",\"Email\":\"alice@example.test\"}");
        AssertStored(Kinds.OidcBrowserCode, new OidcBrowserCodePayload("app", "https://app.example.test/cb", "challenge", "S256", "google"),
            "{\"ClientId\":\"app\",\"RedirectUri\":\"https://app.example.test/cb\",\"CodeChallenge\":\"challenge\",\"CodeChallengeMethod\":\"S256\",\"AuthenticationMethod\":\"google\"}");
        AssertStored(Kinds.CalendarConnectRequest, new CalendarConnectRequestPayload("oidc_1", SqlOSCalendarIntegrationMode.ReadPull, "usr_1", null, "Work", ["calendar.read"], "https://app.example.test/back", "verifier", "https://sqlos.example.test/cb", "https://provider.example.test/token"),
            "{\"OidcConnectionId\":\"oidc_1\",\"Mode\":1,\"UserId\":\"usr_1\",\"OrganizationId\":null,\"DisplayName\":\"Work\",\"Scopes\":[\"calendar.read\"],\"ReturnUri\":\"https://app.example.test/back\",\"CodeVerifier\":\"verifier\",\"CallbackUri\":\"https://sqlos.example.test/cb\",\"TokenEndpoint\":\"https://provider.example.test/token\"}");
        AssertStored(Kinds.IssuerSession, new IssuerSessionPayload("password", Now),
            "{\"AuthenticationMethod\":\"password\",\"AuthenticatedAt\":\"2026-09-30T12:00:00Z\"}");
    }

    [TestMethod]
    public void Tokens_minted_before_a_payload_member_existed_still_read()
    {
        Kinds.IssuerSession.Deserialize("{\"AuthenticationMethod\":\"password\"}")
            .Should().Be(new IssuerSessionPayload("password"));
        Kinds.AuthPagePending.Deserialize("{\"AuthorizationRequestId\":\"req_1\",\"AuthenticationMethod\":\"saml\"}")
            .Should().Be(new PendingAuthorizationPayload("req_1", "saml"));
        Kinds.AuthPageConsent.Deserialize("{\"AuthorizationRequestId\":\"req_1\",\"AuthenticationMethod\":\"password\"}")
            .Should().Be(new PendingConsentPayload("req_1", "password"));
        Kinds.MfaChallenge.Deserialize("{\"Flow\":\"authorization\",\"ClientId\":\"app\",\"AuthenticationMethod\":\"password\",\"FailedAttempts\":1}")!
            .CredentialSignIn.Should().BeFalse();
    }

    private static void AssertStored<TPayload>(TemporaryTokenKind<TPayload> kind, TPayload payload, string expected)
        where TPayload : class
    {
        var stored = kind.Serialize(payload);

        stored.Should().Be(expected, $"{kind.Purpose} payloads keep the 7.2.1 format");
        stored.Should().Be(JsonSerializer.Serialize((object)payload), $"{kind.Purpose} payloads are written as 7.2.1's JsonSerializer.Serialize(object) wrote them");
        JsonSerializer.Serialize(kind.Deserialize(stored), typeof(TPayload)).Should().Be(expected, $"{kind.Purpose} payloads round-trip");
    }
}
