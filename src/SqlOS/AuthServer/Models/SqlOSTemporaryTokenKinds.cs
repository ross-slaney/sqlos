using SqlOS.AuthServer.Contracts;
using SqlOS.Calendar.Services;
using SqlOS.Domain;
using SqlOS.Domain.Events;
using static SqlOS.Domain.TemporaryTokenBindings;
using static SqlOS.Domain.TemporaryTokenLifetime;

namespace SqlOS.AuthServer.Models;

/// <summary>
/// Every purpose SqlOS issues temporary tokens for, one <see cref="TemporaryTokenKind"/> each
/// (<c>docs/architecture/domain-model.md</c> §15, Amendment 1). SqlOS issues, reads and spends its
/// tokens only through these kinds; the purposes, payload formats and lifetimes are exactly 7.2.1's.
/// </summary>
/// <remarks>
/// A configured lifetime names the setting the issuing code passes. Hosts mint tokens of their own
/// purposes through <c>SqlOSCryptoService</c>'s public string API, which uses
/// <see cref="ForHost"/>.
/// </remarks>
internal static class SqlOSTemporaryTokenKinds
{
    /// <summary>A password-reset link, bound to the account and to the client it was requested from.</summary>
    public static TemporaryTokenKind<PasswordResetPayload> PasswordReset { get; } = new(
        Purposes.PasswordReset,
        Configured("SqlOSPasswordResetOptions.TokenLifetime"),
        User | ClientApplication);

    /// <summary>A record of a password-reset request. Never presented; it lives for the request window.</summary>
    public static TemporaryTokenKind<PasswordResetRequestPayload> PasswordResetRequest { get; } = new(
        Purposes.PasswordResetRequest,
        Configured("SqlOSPasswordResetOptions.RateLimitWindow"),
        User | ClientApplication,
        isSingleUse: false);

    /// <summary>An email-verification link for one address of an account. Creating one is audited.</summary>
    public static TemporaryTokenKind<EmailVerificationPayload> EmailVerification { get; } = new(
        Purposes.EmailVerification,
        Fixed(TimeSpan.FromDays(1)),
        User,
        issued: static (tokenId, binding) => new EmailVerificationTokenCreated(tokenId, binding.UserId!));

    /// <summary>An MFA challenge between a first factor and the second; its payload counts failed codes.</summary>
    public static TemporaryTokenKind<SqlOSMfaChallengePayload> MfaChallenge { get; } = new(
        Purposes.MfaChallenge,
        Configured("SqlOSTotpMfaOptions.ChallengeTokenLifetime"),
        User | ClientApplication | Organization);

    /// <summary>An authenticator-app enrollment waiting for its first code.</summary>
    public static TemporaryTokenKind<TotpEnrollmentPayload> TotpEnrollment { get; } = new(
        Purposes.TotpEnrollment,
        Configured("SqlOSTotpMfaOptions.EnrollmentTokenLifetime"),
        User | ClientApplication | Organization);

    /// <summary>A direct-login sign-in waiting for its organization to be chosen.</summary>
    public static TemporaryTokenKind<PendingAuthPayload> PendingAuth { get; } = new(
        Purposes.PendingAuth,
        Configured("SqlOSAuthServerOptions.TemporaryTokenLifetime"),
        User | ClientApplication);

    /// <summary>A hosted sign-in waiting for its organization to be chosen.</summary>
    public static TemporaryTokenKind<PendingAuthorizationPayload> AuthPagePending { get; } = new(
        Purposes.AuthPagePending,
        Fixed(TimeSpan.FromMinutes(10)),
        User | ClientApplication);

    /// <summary>A hosted sign-in waiting for the user's consent.</summary>
    public static TemporaryTokenKind<PendingConsentPayload> AuthPageConsent { get; } = new(
        Purposes.AuthPageConsent,
        Fixed(TimeSpan.FromMinutes(10)),
        User | ClientApplication);

    /// <summary>The handles a hosted authorization continues with after an interstitial step.</summary>
    public static TemporaryTokenKind<AuthorizationContinuationPayload> AuthorizationContinuation { get; } = new(
        Purposes.AuthorizationContinuation,
        Configured("SqlOSTotpMfaOptions.ChallengeTokenLifetime"),
        None);

    /// <summary>A sign-in link, delivered only to the stored address of the account it signs in to.</summary>
    public static TemporaryTokenKind<MagicLinkPayload> MagicLink { get; } = new(
        Purposes.MagicLink,
        Configured("SqlOSMagicLinkOptions.TokenLifetime"),
        User | ClientApplication | Organization);

    /// <summary>An email-code signup waiting for its code.</summary>
    public static TemporaryTokenKind<EmailOtpSignupPayload> EmailOtpSignup { get; } = new(
        Purposes.EmailOtpSignup,
        Configured("SqlOSEmailOtpOptions.ChallengeLifetime"),
        ClientApplication);

    /// <summary>A phone-code signup waiting for its code.</summary>
    public static TemporaryTokenKind<PhoneOtpSignupPayload> PhoneOtpSignup { get; } = new(
        Purposes.PhoneOtpSignup,
        Configured("SqlOSPhoneOtpOptions.ChallengeLifetime"),
        ClientApplication | Organization);

    /// <summary>An upstream OIDC sign-in started by a browser client: the state sent to the provider.</summary>
    public static TemporaryTokenKind<OidcBrowserRequestPayload> OidcBrowserRequest { get; } = new(
        Purposes.OidcBrowserRequest,
        Configured("SqlOSAuthServerOptions.TemporaryTokenLifetime"),
        ClientApplication);

    /// <summary>An upstream OIDC sign-in started from a hosted authorization request: the state sent to the provider.</summary>
    public static TemporaryTokenKind<OidcAuthorizationRequestPayload> OidcAuthorizationRequest { get; } = new(
        Purposes.OidcAuthorizationRequest,
        Configured("SqlOSAuthServerOptions.TemporaryTokenLifetime"),
        ClientApplication | Organization);

    /// <summary>The code a browser client exchanges after an upstream OIDC sign-in.</summary>
    public static TemporaryTokenKind<OidcBrowserCodePayload> OidcBrowserCode { get; } = new(
        Purposes.OidcBrowserCode,
        Fixed(TimeSpan.FromMinutes(5)),
        User | ClientApplication);

    /// <summary>A calendar connection started with a provider: the state sent to it.</summary>
    public static TemporaryTokenKind<CalendarConnectRequestPayload> CalendarConnectRequest { get; } = new(
        Purposes.CalendarConnectRequest,
        Configured("SqlOSCalendarOptions.ConnectSessionLifetime"),
        User | Organization);

    /// <summary>
    /// An issuer-session cookie: presented on every hosted request until it is renewed, signed out
    /// or revoked, so using it does not spend it.
    /// </summary>
    public static TemporaryTokenKind<IssuerSessionPayload> IssuerSession { get; } = new(
        Purposes.IssuerSession,
        Configured("SqlOSSecuritySettings.SessionIdleTimeout"),
        User | Organization | TemporaryTokenBindings.IssuerSession,
        isSingleUse: false);

    /// <summary>Every kind SqlOS issues, one per purpose.</summary>
    public static IReadOnlyList<TemporaryTokenKind> All { get; } =
    [
        PasswordReset,
        PasswordResetRequest,
        EmailVerification,
        MfaChallenge,
        TotpEnrollment,
        PendingAuth,
        AuthPagePending,
        AuthPageConsent,
        AuthorizationContinuation,
        MagicLink,
        EmailOtpSignup,
        PhoneOtpSignup,
        OidcBrowserRequest,
        OidcAuthorizationRequest,
        OidcBrowserCode,
        CalendarConnectRequest,
        IssuerSession
    ];

    /// <summary>
    /// The kind behind <c>SqlOSCryptoService</c>'s public string API, through which a host issues
    /// and spends tokens of its own purposes (for example an OIDC hand-off), exactly as 7.x
    /// allowed: any binding, any payload, and the host's lifetime or the default
    /// <c>SqlOSAuthServerOptions.TemporaryTokenLifetime</c>.
    /// </summary>
    public static TemporaryTokenKind<object> ForHost(string purpose)
        => new(purpose, Configured("the host's lifetime or SqlOSAuthServerOptions.TemporaryTokenLifetime"), TemporaryTokenBindings.All);

    /// <summary>The stored purpose strings, which are part of the stored data and never change.</summary>
    internal static class Purposes
    {
        public const string PasswordReset = "password_reset";
        public const string PasswordResetRequest = "password_reset_request";
        public const string EmailVerification = "email_verification";
        public const string MfaChallenge = "mfa_challenge";
        public const string TotpEnrollment = "mfa_totp_enrollment";
        public const string PendingAuth = "pending_auth";
        public const string AuthPagePending = "auth_page_pending";
        public const string AuthPageConsent = "auth_page_consent";
        public const string AuthorizationContinuation = "authorization_continue";
        public const string MagicLink = "auth.magic_link";
        public const string EmailOtpSignup = "email_otp_signup";
        public const string PhoneOtpSignup = "phone_otp_signup";
        public const string OidcBrowserRequest = "oidc_browser_request";
        public const string OidcAuthorizationRequest = "oidc_authorization_request";
        public const string OidcBrowserCode = "oidc_browser_code";
        public const string CalendarConnectRequest = "calendar_connect_request";
        public const string IssuerSession = "auth_page_session";

        /// <summary>
        /// The SAML authorization code SqlOS issued as a temporary token before #165 moved every
        /// code to <c>SqlOSAuthorizationCodes</c>. Nothing issues it any more; revocation still
        /// withdraws rows an upgraded database may hold.
        /// </summary>
        public const string LegacySamlAuthorizationCode = "auth_code";
    }
}
