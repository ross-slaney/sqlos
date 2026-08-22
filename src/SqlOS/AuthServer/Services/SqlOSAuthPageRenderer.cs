using System.Globalization;
using System.Net;
using SqlOS.AuthServer.Contracts;
using SqlOS.Security;

namespace SqlOS.AuthServer.Services;

public static class SqlOSAuthPageRenderer
{
    public static string RenderPage(SqlOSAuthPageViewModel model)
    {
        var normalizedMode = NormalizeMode(model.Mode);
        var isStackedLayout = string.Equals(model.Settings.Layout, "stacked", StringComparison.OrdinalIgnoreCase);
        var title = string.IsNullOrWhiteSpace(model.Settings.PageTitle)
            ? "Sign in"
            : model.Settings.PageTitle;
        var subtitle = model.Settings.PageSubtitle ?? string.Empty;
        var subtitleMarkup = string.IsNullOrWhiteSpace(subtitle)
            ? string.Empty
            : $"<p class=\"brand-subtitle\">{Html(subtitle)}</p>";
        var primaryColor = Css(model.Settings.PrimaryColor, "#4f46e5");
        var accentColor = Css(model.Settings.AccentColor, "#111827");
        var backgroundColor = Css(model.Settings.BackgroundColor, "#f8fafc");
        var isDarkTheme = IsDarkColor(backgroundColor);
        var textColor = isDarkTheme ? "#f8fafc" : accentColor;
        var mutedColor = isDarkTheme ? "rgba(248,250,252,0.72)" : "#6b7280";
        var shellColor = isDarkTheme ? "rgba(9,10,12,0.96)" : "rgba(255,255,255,0.96)";
        var panelColor = isDarkTheme ? "#16181d" : "#ffffff";
        var borderColor = isDarkTheme ? "rgba(255,255,255,0.14)" : "#d9dde6";
        var borderStrongColor = isDarkTheme ? "rgba(255,255,255,0.20)" : "#cfd5df";
        var inputBackground = isDarkTheme ? "#101217" : "#ffffff";
        var buttonTextColor = GetContrastingTextColor(primaryColor);
        var shadowColor = isDarkTheme
            ? "0 28px 90px rgba(0,0,0,0.36)"
            : "0 28px 90px rgba(15,23,42,0.10)";
        var logoMarkup = string.IsNullOrWhiteSpace(model.Settings.LogoBase64)
            ? "<div class=\"logo-fallback\">SqlOS</div>"
            : $"<img class=\"logo\" src=\"{Html(model.Settings.LogoBase64)}\" alt=\"Logo\" />";
        var requestIdInput = string.IsNullOrWhiteSpace(model.AuthorizationRequestId)
            ? string.Empty
            : $"<input type=\"hidden\" name=\"requestId\" value=\"{Html(model.AuthorizationRequestId)}\" />";
        var invitationTokenInput = string.IsNullOrWhiteSpace(model.InvitationToken)
            ? string.Empty
            : $"<input type=\"hidden\" name=\"invitationToken\" value=\"{Html(model.InvitationToken)}\" />";
        var deviceUserCodeInput = string.IsNullOrWhiteSpace(model.DeviceUserCode)
            ? string.Empty
            : $"<input type=\"hidden\" name=\"deviceUserCode\" value=\"{Html(model.DeviceUserCode)}\" />";
        var mfaTokenInput = string.IsNullOrWhiteSpace(model.MfaToken)
            ? string.Empty
            : $"<input type=\"hidden\" name=\"mfaToken\" value=\"{Html(model.MfaToken)}\" />";
        var mfaMethodValues = model.MfaMethods ?? Array.Empty<string>();
        var mfaMethods = mfaMethodValues.Count == 0
            ? "an authenticator app or recovery code"
            : string.Join(" or ", mfaMethodValues.Select(static method => string.Equals(method, SqlOSMfaFactorTypes.Totp, StringComparison.OrdinalIgnoreCase)
                ? "an authenticator app"
                : "a recovery code"));
        var emailValue = Html(model.Email ?? string.Empty);
        var emailReadonly = model.Invitation == null ? string.Empty : " readonly";
        var encodedMode = Html(normalizedMode);
        var supportsPassword = model.Settings.LocalPasswordRuntimeEnabled
            && SupportsCredentialType(model.Settings.EnabledCredentialTypes, "password");
        var supportsEmailOtp = model.Settings.EmailOtpRuntimeConfigured
            && SupportsCredentialType(model.Settings.EnabledCredentialTypes, "email_otp");
        var supportsMagicLink = model.Settings.MagicLinkRuntimeConfigured
            && SupportsCredentialType(model.Settings.EnabledCredentialTypes, "magic_link");
        var supportsPhoneOtp = model.Settings.PhoneOtpRuntimeConfigured
            && SupportsCredentialType(model.Settings.EnabledCredentialTypes, "phone_otp");
        var supportsPasswordSignup = supportsPassword && model.Settings.EnablePasswordSignup;
        var supportsEmailOtpSignup = supportsEmailOtp;
        var supportsPhoneOtpSignup = model.Invitation == null && supportsPhoneOtp;
        var supportsInvitationSignup = model.Invitation != null && supportsEmailOtpSignup;
        var supportsSignup = supportsPasswordSignup || supportsEmailOtpSignup || supportsPhoneOtpSignup;
        var signupLink = supportsSignup
            ? $"<a class=\"secondary-link\" href=\"{Html(AuthPath(model, "/signup", model.AuthorizationRequestId))}\">Get started</a>"
            : string.Empty;
        var loginLink = $"<a class=\"secondary-link\" href=\"{Html(AuthPath(model, "/login", model.AuthorizationRequestId))}\">Sign in</a>";
        var passwordLink = supportsPassword
            ? $"<a class=\"secondary-link\" href=\"{Html(AuthPathWithQuery(model, "/login", model.AuthorizationRequestId, ("email", model.Email)))}\">Use password instead</a>"
            : string.Empty;
        var emailOtpLink = supportsEmailOtp
            ? $"<a class=\"secondary-link\" href=\"{Html(AuthPathWithQuery(model, "/login/email-otp", model.AuthorizationRequestId, ("email", model.Email)))}\">Use an email code instead</a>"
            : string.Empty;
        var magicLinkLink = supportsMagicLink
            ? $"<a class=\"secondary-link\" href=\"{Html(AuthPathWithQuery(model, "/login/magic-link", model.AuthorizationRequestId, ("email", model.Email)))}\">Email me a sign-in link</a>"
            : string.Empty;
        var phoneOtpLink = supportsPhoneOtp
            ? $"<a class=\"secondary-link\" href=\"{Html(AuthPathWithQuery(model, "/login/phone-otp", model.AuthorizationRequestId, ("phoneNumber", model.PhoneNumber)))}\">Use a phone code instead</a>"
            : string.Empty;
        var forgotPasswordLink = supportsPassword
            ? $"<a class=\"secondary-link\" href=\"{Html(AuthPathWithQuery(model, "/password/forgot", model.AuthorizationRequestId, ("email", model.Email)))}\">Forgot password?</a>"
            : string.Empty;
        var phoneOtpSignupLink = supportsPhoneOtpSignup
            ? $"<a class=\"secondary-link\" href=\"{Html(AuthPathWithQuery(model, "/signup/phone-otp", model.AuthorizationRequestId, ("phoneNumber", model.PhoneNumber)))}\">Create account with phone code</a>"
            : string.Empty;
        var signInAgainLink = $"<a class=\"secondary-link\" href=\"{Html(AuthPath(model, "/login"))}\">Sign in again</a>";
        var signOutLink = $"<a class=\"secondary-link\" href=\"{Html(AuthPath(model, "/logout"))}\">Sign out</a>";
        var errorMarkup = BuildCallout("error", model.Error);
        var infoMarkup = BuildCallout(
            "info",
            model.Info,
            model.OmittedOpenId ? SqlOSOpenIdScopeWarnings.OmittedGrantedOpenIdCode : null);
        var invitationMarkup = RenderInvitationSummary(model.Invitation);
        var organizationField = model.Invitation == null
            ? """
                  <label class="field">
                    <span>Organization name</span>
                    <input name="organizationName" placeholder="Optional" autocomplete="organization" />
                  </label>
              """
            : string.Empty;
        var signupContent = supportsInvitationSignup
            ? $$"""
                {{RenderPanelIntro("Create account", "Create your account to accept this invitation.")}}
                {{invitationMarkup}}
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/signup/invitation/submit"))}}">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  {{deviceUserCodeInput}}
                  <input type="hidden" name="mode" value="{{encodedMode}}" />
                  <label class="field">
                    <span>Display name</span>
                    <input name="displayName" value="{{Html(model.DisplayName ?? string.Empty)}}" placeholder="Jane Doe" autocomplete="name" required />
                  </label>
                  <label class="field">
                    <span>Email</span>
                    <input name="email" type="email" value="{{emailValue}}" autocomplete="email" readonly required />
                  </label>
                  {{RenderPrimaryAction("Create account", "Creating account")}}
                </form>
                {{RenderProvidersSection(model)}}
                {{RenderFooterPrompt("Already have an account?", loginLink)}}
                """
            : supportsPasswordSignup
            ? $$"""
                {{RenderPanelIntro("Create account", "Use email and password to set up your account.")}}
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/signup/submit"))}}">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  {{deviceUserCodeInput}}
                  <input type="hidden" name="mode" value="{{encodedMode}}" />
                  <label class="field">
                    <span>Display name</span>
                    <input name="displayName" value="{{Html(model.DisplayName ?? string.Empty)}}" placeholder="Jane Doe" autocomplete="name" required />
                  </label>
                  <label class="field">
                    <span>Email</span>
                    <input name="email" type="email" value="{{emailValue}}" placeholder="Your email address" autocomplete="email" required{{emailReadonly}} />
                  </label>
                  <label class="field">
                    <span>Password</span>
                    <input name="password" type="password" placeholder="Create a password" autocomplete="new-password" required />
                  </label>
                  {{organizationField}}
                {{RenderPrimaryAction("Create account", "Creating account")}}
                </form>
                {{RenderProvidersSection(model)}}
                {{RenderFooterLinks(phoneOtpSignupLink)}}
                {{RenderFooterPrompt("Already have an account?", loginLink)}}
                """
            : supportsEmailOtpSignup
                ? $$"""
                    {{RenderPanelIntro("Create account", "Verify your email with a one-time code to create your account.")}}
                    <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/signup/email-otp/start"))}}">
                      {{requestIdInput}}
                      {{invitationTokenInput}}
                      {{deviceUserCodeInput}}
                      <input type="hidden" name="mode" value="{{encodedMode}}" />
                      <label class="field">
                        <span>Display name</span>
                        <input name="displayName" value="{{Html(model.DisplayName ?? string.Empty)}}" placeholder="Jane Doe" autocomplete="name" required />
                      </label>
                      <label class="field">
                        <span>Email</span>
                        <input name="email" type="email" value="{{emailValue}}" placeholder="Your email address" autocomplete="email" required{{emailReadonly}} />
                      </label>
                      {{organizationField}}
                      {{RenderPrimaryAction("Email me a code", "Sending code")}}
                    </form>
                    {{RenderProvidersSection(model)}}
                    {{RenderFooterLinks(phoneOtpSignupLink)}}
                    {{RenderFooterPrompt("Already have an account?", loginLink)}}
                    """
            : supportsPhoneOtpSignup
                ? $$"""
                    {{RenderPanelIntro("Create account", "Verify your phone with a one-time code to create your account.")}}
                    <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/signup/phone-otp/start"))}}">
                      {{requestIdInput}}
                      {{invitationTokenInput}}
                      {{deviceUserCodeInput}}
                      <input type="hidden" name="mode" value="{{encodedMode}}" />
                      <label class="field">
                        <span>Display name</span>
                        <input name="displayName" value="{{Html(model.DisplayName ?? string.Empty)}}" placeholder="Jane Doe" autocomplete="name" required />
                      </label>
                      <label class="field">
                        <span>Phone</span>
                        <input name="phoneNumber" type="tel" value="{{Html(model.PhoneNumber ?? string.Empty)}}" placeholder="+1 202 555 0105" autocomplete="tel" required />
                      </label>
                      {{organizationField}}
                      {{RenderPrimaryAction("Text me a code", "Sending code")}}
                    </form>
                    {{RenderProvidersSection(model)}}
                    {{RenderFooterPrompt("Already have an account?", loginLink)}}
                    """
                : $$"""
                    {{RenderPanelIntro("Create account", "Account creation is not available.")}}
                    {{RenderFooterPrompt("Already have an account?", loginLink)}}
                    """;

        var content = normalizedMode switch
        {
            "invite" => $$"""
                {{RenderPanelIntro("Accept Invitation", "Continue with the invited email address to join the organization.")}}
                {{invitationMarkup}}
                {{(supportsEmailOtp ? $$"""
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/login/email-otp/start"))}}">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  <input type="hidden" name="email" value="{{emailValue}}" />
                  {{RenderPrimaryAction("Email me a sign-in code", "Sending code")}}
                </form>
                """ : string.Empty)}}
                {{(supportsMagicLink ? $$"""
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/login/magic-link/start"))}}">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  <input type="hidden" name="email" value="{{emailValue}}" />
                  {{RenderPrimaryAction("Email me a sign-in link", "Sending link")}}
                </form>
                """ : string.Empty)}}
                {{(supportsInvitationSignup ? $$"""
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/signup/invitation/submit"))}}">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  <label class="field">
                    <span>Display name</span>
                    <input name="displayName" value="{{Html(model.DisplayName ?? string.Empty)}}" placeholder="Jane Doe" autocomplete="name" required />
                  </label>
                  <input type="hidden" name="email" value="{{emailValue}}" />
                  {{RenderPrimaryAction("Create account", "Creating account")}}
                </form>
                """ : string.Empty)}}
                {{(!supportsInvitationSignup && supportsPasswordSignup ? $$"""
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/signup/submit"))}}">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  <label class="field">
                    <span>Display name</span>
                    <input name="displayName" value="{{Html(model.DisplayName ?? string.Empty)}}" placeholder="Jane Doe" autocomplete="name" required />
                  </label>
                  <input type="hidden" name="email" value="{{emailValue}}" />
                  <label class="field">
                    <span>Password</span>
                    <input name="password" type="password" placeholder="Create a password" autocomplete="new-password" required />
                  </label>
                  {{RenderPrimaryAction("Create account with password", "Creating account")}}
                </form>
                """ : string.Empty)}}
                {{(supportsPassword ? $$"""
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/login/password"))}}">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  <input type="hidden" name="email" value="{{emailValue}}" />
                  <label class="field">
                    <span>Password</span>
                    <input name="password" type="password" placeholder="Your password" autocomplete="current-password" required />
                  </label>
                  {{RenderPrimaryAction("Continue with password", "Signing in")}}
                </form>
                """ : string.Empty)}}
                {{(!supportsEmailOtp && !supportsMagicLink && !supportsPassword && !supportsInvitationSignup && !supportsPasswordSignup ? BuildCallout("error", "No compatible sign-in method is enabled for this invitation.") : string.Empty)}}
                {{RenderProvidersSection(model)}}
                """,
            "device" => $$"""
                {{RenderPanelIntro("Connect CLI", "Enter the code shown in your terminal to continue.")}}
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/device/verify"))}}">
                  <label class="field">
                    <span>Device code</span>
                    <input name="userCode" value="{{Html(model.DeviceUserCode ?? string.Empty)}}" placeholder="ABCD-EFGH" autocomplete="one-time-code" required autofocus />
                  </label>
                  {{RenderPrimaryAction("Continue", "Checking code")}}
                </form>
                """,
            "device-approve" => $$"""
                {{RenderPanelIntro("Approve CLI Access", "A command-line app is asking to access this account.")}}
                {{RenderDeviceSummary(model.DeviceAuthorization)}}
                {{(model.OrganizationSelection.Count > 1 ? $$"""
                <form class="auth-form organization-form" method="post" action="{{Html(model.BasePath.TrimEnd('/'))}}/device/approve">
                  {{requestIdInput}}
                  <input type="hidden" name="userCode" value="{{Html(model.DeviceUserCode ?? string.Empty)}}" />
                  <div class="organization-list">{{RenderOrganizationOptions(model.OrganizationSelection)}}</div>
                  {{RenderPrimaryAction("Approve CLI access", "Approving")}}
                </form>
                """ : $$"""
                <form class="auth-form" method="post" action="{{Html(model.BasePath.TrimEnd('/'))}}/device/approve">
                  {{requestIdInput}}
                  <input type="hidden" name="userCode" value="{{Html(model.DeviceUserCode ?? string.Empty)}}" />
                  {{RenderPrimaryAction("Approve CLI access", "Approving")}}
                </form>
                """)}}
                <form class="auth-form" method="post" action="{{Html(model.BasePath.TrimEnd('/'))}}/device/deny">
                  {{requestIdInput}}
                  <input type="hidden" name="userCode" value="{{Html(model.DeviceUserCode ?? string.Empty)}}" />
                  <button class="secondary-action" type="submit">Deny request</button>
                </form>
                """,
            "device-approved" => $$"""
                <div class="state-card">
                  <span class="state-icon">OK</span>
                  <div class="state-copy">
                    <strong>CLI access approved.</strong>
                    <p>You can return to your terminal.</p>
                  </div>
                </div>
                """,
            "consent" => $$"""
                {{RenderPanelIntro("Authorize Access", $"{model.ClientName ?? "An application"} is asking to access your account.")}}
                {{RenderConsentSummary(model)}}
                <form class="auth-form" method="post" action="{{Html(model.BasePath.TrimEnd('/'))}}/consent/approve">
                  {{requestIdInput}}
                  <input type="hidden" name="consentToken" value="{{Html(model.ConsentToken ?? string.Empty)}}" />
                  {{RenderPrimaryAction("Allow access", "Approving")}}
                </form>
                <form class="auth-form" method="post" action="{{Html(model.BasePath.TrimEnd('/'))}}/consent/deny">
                  {{requestIdInput}}
                  <input type="hidden" name="consentToken" value="{{Html(model.ConsentToken ?? string.Empty)}}" />
                  <button class="secondary-action" type="submit">Deny request</button>
                </form>
                """,
            "signup" => signupContent,
            "password" => $$"""
                {{RenderPanelIntro("Password", "Continue with your email and password.")}}
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/login/password"))}}">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  {{deviceUserCodeInput}}
                  <label class="field">
                    <span>Email</span>
                    <input name="email" type="email" value="{{emailValue}}" placeholder="Your email address" autocomplete="email" required{{emailReadonly}} />
                  </label>
                  <label class="field">
                    <span>Password</span>
                    <input name="password" type="password" placeholder="Your password" autocomplete="current-password" required />
                  </label>
                  {{RenderPrimaryAction("Continue", "Signing in")}}
                </form>
                {{RenderFooterLinks(string.Join(string.Empty, new[] { forgotPasswordLink, emailOtpLink, magicLinkLink, phoneOtpLink }.Where(link => !string.IsNullOrWhiteSpace(link))))}}
                {{RenderProvidersSection(model)}}
                {{RenderFooterPrompt("Don't have an account?", signupLink)}}
                """,
            "forgot-password" => $$"""
                {{RenderPanelIntro("Reset Password", "Enter your email address and we'll send a reset link if the account can be reset.")}}
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/password/forgot/submit"))}}">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  {{deviceUserCodeInput}}
                  <label class="field">
                    <span>Email</span>
                    <input name="email" type="email" value="{{emailValue}}" placeholder="Your email address" autocomplete="email" required{{emailReadonly}} />
                  </label>
                  {{RenderPrimaryAction("Send reset email", "Sending reset email")}}
                </form>
                {{RenderFooterLinks(loginLink)}}
                """,
            "forgot-password-sent" => $$"""
                <div class="state-card">
                  <span class="state-icon">OK</span>
                  <div class="state-copy">
                    <strong>Check your email.</strong>
                    <p>If the account can be reset, a password reset link is on the way.</p>
                  </div>
                </div>
                {{RenderFooterLinks(loginLink)}}
                """,
            "email-otp" => $$"""
                {{RenderPanelIntro("Email Code", "Get a one-time code sent to your email address.")}}
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/login/email-otp/start"))}}">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  {{deviceUserCodeInput}}
                  <label class="field">
                    <span>Email</span>
                    <input name="email" type="email" value="{{emailValue}}" placeholder="Your email address" autocomplete="email" required{{emailReadonly}} />
                  </label>
                  {{RenderPrimaryAction("Email me a code", "Sending code")}}
                </form>
                {{RenderFooterLinks(string.Join(string.Empty, new[] { passwordLink, magicLinkLink, phoneOtpLink, signupLink }.Where(link => !string.IsNullOrWhiteSpace(link))))}}
                {{RenderProvidersSection(model)}}
                """,
            "magic-link" => $$"""
                {{RenderPanelIntro("Email Link", "Get a one-time sign-in link sent to your email address.")}}
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/login/magic-link/start"))}}">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  {{deviceUserCodeInput}}
                  <label class="field">
                    <span>Email</span>
                    <input name="email" type="email" value="{{emailValue}}" placeholder="Your email address" autocomplete="email" required{{emailReadonly}} />
                  </label>
                  {{RenderPrimaryAction("Email me a link", "Sending link")}}
                </form>
                {{RenderFooterLinks(string.Join(string.Empty, new[] { passwordLink, emailOtpLink, phoneOtpLink, signupLink }.Where(link => !string.IsNullOrWhiteSpace(link))))}}
                {{RenderProvidersSection(model)}}
                """,
            "magic-link-sent" => $$"""
                <div class="state-card">
                  <span class="state-icon">OK</span>
                  <div class="state-copy">
                    <strong>Check your email.</strong>
                    <p>If the account exists, a sign-in link is on the way.</p>
                  </div>
                </div>
                {{RenderFooterLinks(string.Join(string.Empty, new[] {
                    $"<a class=\"secondary-link\" href=\"{Html(AuthPathWithQuery(model, "/login/magic-link", model.AuthorizationRequestId, ("email", model.Email)))}\">Request another link</a>",
                    emailOtpLink,
                    passwordLink
                }.Where(link => !string.IsNullOrWhiteSpace(link))))}}
                """,
            "magic-link-confirm" => $$"""
                {{RenderPanelIntro("Continue Sign In", "Confirm this browser should use the emailed sign-in link.")}}
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/login/magic-link/complete"))}}">
                  <input type="hidden" name="token" value="{{Html(model.PendingToken ?? string.Empty)}}" />
                  {{RenderPrimaryAction("Continue sign in", "Signing in")}}
                </form>
                {{RenderFooterLinks(loginLink)}}
                """,
            "phone-otp" => $$"""
                {{RenderPanelIntro("Phone Code", "Get a one-time code sent to your phone.")}}
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/login/phone-otp/start"))}}">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  {{deviceUserCodeInput}}
                  <label class="field">
                    <span>Phone</span>
                    <input name="phoneNumber" type="tel" value="{{Html(model.PhoneNumber ?? string.Empty)}}" placeholder="+1 202 555 0105" autocomplete="tel" required />
                  </label>
                  {{RenderPrimaryAction("Text me a code", "Sending code")}}
                </form>
                {{RenderFooterLinks(string.Join(string.Empty, new[] { passwordLink, emailOtpLink, magicLinkLink, signupLink }.Where(link => !string.IsNullOrWhiteSpace(link))))}}
                {{RenderProvidersSection(model)}}
                """,
            "phone-otp-verify" => $$"""
                {{RenderPanelIntro("Enter Code", "Use the one-time code we sent to your phone.")}}
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/login/phone-otp/verify"))}}">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  {{deviceUserCodeInput}}
                  <input type="hidden" name="challengeToken" value="{{Html(model.ChallengeToken ?? string.Empty)}}" />
                  <input type="hidden" name="phoneNumber" value="{{Html(model.PhoneNumber ?? string.Empty)}}" />
                  <label class="field">
                    <span>Code</span>
                    <input name="code" inputmode="numeric" autocomplete="one-time-code" placeholder="123456" required autofocus />
                  </label>
                  {{RenderPrimaryAction("Verify code", "Verifying code")}}
                </form>
                {{RenderFooterLinks(string.Join(string.Empty, new[] {
                    $"<a class=\"secondary-link\" href=\"{Html(AuthPathWithQuery(model, "/login/phone-otp", model.AuthorizationRequestId, ("phoneNumber", model.PhoneNumber)))}\">Send a new code</a>",
                    passwordLink,
                    emailOtpLink,
                    magicLinkLink
                }.Where(link => !string.IsNullOrWhiteSpace(link))))}}
                """,
            "phone-otp-signup" => $$"""
                {{RenderPanelIntro("Create account", "Verify your phone with a one-time code to create your account.")}}
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/signup/phone-otp/start"))}}">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  {{deviceUserCodeInput}}
                  <input type="hidden" name="mode" value="{{encodedMode}}" />
                  <label class="field">
                    <span>Display name</span>
                    <input name="displayName" value="{{Html(model.DisplayName ?? string.Empty)}}" placeholder="Jane Doe" autocomplete="name" required />
                  </label>
                  <label class="field">
                    <span>Phone</span>
                    <input name="phoneNumber" type="tel" value="{{Html(model.PhoneNumber ?? string.Empty)}}" placeholder="+1 202 555 0105" autocomplete="tel" required />
                  </label>
                  {{organizationField}}
                  {{RenderPrimaryAction("Text me a code", "Sending code")}}
                </form>
                {{RenderFooterPrompt("Already have an account?", loginLink)}}
                """,
            "phone-otp-signup-verify" => $$"""
                {{RenderPanelIntro("Enter Code", "Use the one-time code we sent to your phone to create your account.")}}
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/signup/phone-otp/verify"))}}">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  {{deviceUserCodeInput}}
                  <input type="hidden" name="signupToken" value="{{Html(model.SignupToken ?? string.Empty)}}" />
                  <input type="hidden" name="challengeToken" value="{{Html(model.ChallengeToken ?? string.Empty)}}" />
                  <input type="hidden" name="phoneNumber" value="{{Html(model.PhoneNumber ?? string.Empty)}}" />
                  <label class="field">
                    <span>Code</span>
                    <input name="code" inputmode="numeric" autocomplete="one-time-code" placeholder="123456" required autofocus />
                  </label>
                  {{RenderPrimaryAction("Verify and create account", "Verifying code")}}
                </form>
                {{RenderFooterLinks($"<a class=\"secondary-link\" href=\"{Html(AuthPathWithQuery(model, "/signup/phone-otp", model.AuthorizationRequestId, ("phoneNumber", model.PhoneNumber)))}\">Start over</a>")}}
                """,
            "email-otp-verify" => $$"""
                {{RenderPanelIntro("Enter Code", "Use the one-time code we sent to your email address.")}}
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/login/email-otp/verify"))}}">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  {{deviceUserCodeInput}}
                  <input type="hidden" name="challengeToken" value="{{Html(model.ChallengeToken ?? string.Empty)}}" />
                  <input type="hidden" name="email" value="{{emailValue}}" />
                  <label class="field">
                    <span>Code</span>
                    <input name="code" inputmode="numeric" autocomplete="one-time-code" placeholder="123456" required autofocus />
                  </label>
                  {{RenderPrimaryAction("Verify code", "Verifying code")}}
                </form>
                {{RenderFooterLinks(string.Join(string.Empty, new[] {
                    $"<a class=\"secondary-link\" href=\"{Html(AuthPathWithQuery(model, "/login/email-otp", model.AuthorizationRequestId, ("email", model.Email)))}\">Send a new code</a>",
                    passwordLink,
                    magicLinkLink,
                    phoneOtpLink
                }.Where(link => !string.IsNullOrWhiteSpace(link))))}}
                """,
            "email-otp-signup-verify" => $$"""
                {{RenderPanelIntro("Enter Code", "Use the one-time code we sent to your email address to create your account.")}}
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/signup/email-otp/verify"))}}">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  {{deviceUserCodeInput}}
                  <input type="hidden" name="signupToken" value="{{Html(model.SignupToken ?? string.Empty)}}" />
                  <input type="hidden" name="challengeToken" value="{{Html(model.ChallengeToken ?? string.Empty)}}" />
                  <input type="hidden" name="email" value="{{emailValue}}" />
                  <label class="field">
                    <span>Code</span>
                    <input name="code" inputmode="numeric" autocomplete="one-time-code" placeholder="123456" required autofocus />
                  </label>
                  {{RenderPrimaryAction("Verify and create account", "Verifying code")}}
                </form>
                {{RenderFooterLinks($"<a class=\"secondary-link\" href=\"{Html(AuthPath(model, "/signup", model.AuthorizationRequestId))}\">Start over</a>")}}
                """,
            "organization" => $$"""
                {{RenderPanelIntro("Organization", "Choose the workspace you want to continue into.")}}
                <form class="auth-form organization-form" method="post" action="{{Html(AuthPath(model, "/login/select-organization"))}}">
                  {{requestIdInput}}
                  <input type="hidden" name="pendingToken" value="{{Html(model.PendingToken ?? string.Empty)}}" />
                  <div class="organization-list">{{RenderOrganizationOptions(model.OrganizationSelection)}}</div>
                </form>
                {{RenderFooterLinks(loginLink)}}
                """,
            "mfa" => $$"""
                {{RenderPanelIntro("Two-step verification", $"Enter a code from {mfaMethods}.")}}
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/mfa/verify"))}}">
                  {{requestIdInput}}
                  {{mfaTokenInput}}
                  <label class="field">
                    <span>Code</span>
                    <input name="code" inputmode="numeric" autocomplete="one-time-code" placeholder="123456" required autofocus />
                  </label>
                  {{RenderPrimaryAction("Verify", "Verifying")}}
                </form>
                {{RenderFooterLinks(loginLink)}}
                """,
            "mfa-enroll" => $$"""
                {{RenderPanelIntro("Add authenticator app", "Use an authenticator app to add a second step before continuing.")}}
                <div class="qr-panel">
                  <img class="qr-code" src="{{Html(model.TotpQrCodeDataUrl ?? string.Empty)}}" alt="Authenticator setup QR code" />
                  <p>Scan this QR code with your authenticator app.</p>
                </div>
                <details class="manual-setup">
                  <summary>Use manual setup</summary>
                  <div class="secret-panel">
                    <span>Setup key</span>
                    <code>{{Html(model.TotpSecret ?? string.Empty)}}</code>
                  </div>
                  <div class="secret-panel">
                    <span>Provisioning URI</span>
                    <code>{{Html(model.TotpProvisioningUri ?? string.Empty)}}</code>
                  </div>
                </details>
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/mfa/totp/enroll/verify"))}}">
                  {{requestIdInput}}
                  {{mfaTokenInput}}
                  <input type="hidden" name="enrollmentToken" value="{{Html(model.EnrollmentToken ?? string.Empty)}}" />
                  <label class="field">
                    <span>Code</span>
                    <input name="code" inputmode="numeric" autocomplete="one-time-code" placeholder="123456" required autofocus />
                  </label>
                  {{RenderPrimaryAction("Verify and continue", "Verifying")}}
                </form>
                """,
            "logged-out" => $$"""
                <div class="state-card">
                  <span class="state-icon">OK</span>
                  <div class="state-copy">
                    <strong>You are signed out.</strong>
                    <p>Your session has ended. Return whenever you are ready to continue.</p>
                  </div>
                </div>
                {{RenderFooterLinks(signInAgainLink)}}
                """,
            "signed-in" => $$"""
                <div class="state-card">
                  <span class="state-icon">OK</span>
                  <div class="state-copy">
                    <strong>You are signed in.</strong>
                    <p>Your SqlOS auth session is active. Return to the application that sent you here to continue.</p>
                  </div>
                </div>
                {{RenderFooterLinks(signOutLink)}}
                """,
            "signed-up" => $$"""
                <div class="state-card">
                  <span class="state-icon">OK</span>
                  <div class="state-copy">
                    <strong>Your account is ready.</strong>
                    <p>Your SqlOS auth session is active. Return to the application that sent you here to continue.</p>
                  </div>
                </div>
                {{RenderFooterLinks(signOutLink)}}
                """,
            "invitation-accepted" => $$"""
                <div class="state-card">
                  <span class="state-icon">OK</span>
                  <div class="state-copy">
                    <strong>Invitation accepted.</strong>
                    <p>Your SqlOS auth session is active. Return to the application that sent you here to continue.</p>
                  </div>
                </div>
                {{RenderFooterLinks(signOutLink)}}
                """,
            _ => $$"""
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/login/identify"))}}" data-flow-kind="hrd">
                  {{requestIdInput}}
                  {{invitationTokenInput}}
                  {{deviceUserCodeInput}}
                  <label class="field">
                    <span>Email</span>
                    <input name="email" type="email" value="{{emailValue}}" placeholder="Your email address" autocomplete="email" required{{emailReadonly}} />
                  </label>
                  {{RenderPrimaryAction("Continue", "Checking email")}}
                  <div class="flow-status" hidden>
                    <span class="loader loader-sm" aria-hidden="true"></span>
                    <span>Checking your sign-in method...</span>
                  </div>
                </form>
                {{RenderProvidersSection(model)}}
                {{RenderFooterLinks(string.Join(string.Empty, new[] { phoneOtpLink, emailOtpLink }.Where(link => !string.IsNullOrWhiteSpace(link))))}}
                {{RenderFooterPrompt("Don't have an account?", signupLink)}}
                """
        };

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>{{Html(title)}}</title>
          <style {{SqlOSCspNonce.Attribute}}>
            :root {
              --page-bg: {{backgroundColor}};
              --primary: {{primaryColor}};
              --accent: {{accentColor}};
              --text: {{textColor}};
              --muted: {{mutedColor}};
              --shell: {{shellColor}};
              --panel: {{panelColor}};
              --border: {{borderColor}};
              --border-strong: {{borderStrongColor}};
              --input-bg: {{inputBackground}};
              --button-text: {{buttonTextColor}};
              --shadow: {{shadowColor}};
            }
            * { box-sizing: border-box; }
            html, body { min-height: 100%; }
            body {
              margin: 0;
              font-family: Inter, "Segoe UI", system-ui, sans-serif;
              color: var(--text);
              background:
                radial-gradient(circle at top, color-mix(in srgb, var(--primary) 10%, transparent) 0%, transparent 38%),
                linear-gradient(180deg, color-mix(in srgb, var(--page-bg) 92%, white) 0%, var(--page-bg) 100%);
            }
            body::before {
              content: "";
              position: fixed;
              inset: 0;
              pointer-events: none;
              background:
                linear-gradient(color-mix(in srgb, var(--accent) 3%, transparent) 1px, transparent 1px),
                linear-gradient(90deg, color-mix(in srgb, var(--accent) 3%, transparent) 1px, transparent 1px);
              background-size: 40px 40px;
              opacity: 0.2;
            }
            h1, h2, p, strong, small, span { margin: 0; }
            input, button { font: inherit; }
            [hidden] { display: none !important; }
            .page-shell {
              min-height: 100vh;
              padding: 28px 16px;
              display: grid;
              place-items: center;
            }
            .auth-shell {
              width: min(calc(100vw - 32px), {{(isStackedLayout ? "760px" : "840px")}});
            }
            .auth-shell.split {
              max-width: 840px;
            }
            .auth-shell.stacked {
              max-width: 760px;
            }
            .auth-frame {
              background: var(--shell);
              border: 1px solid var(--border);
              border-radius: 32px;
              box-shadow: var(--shadow);
              padding: clamp(24px, 4vw, 40px);
            }
            .brand-header {
              display: grid;
              justify-items: center;
              gap: 14px;
              text-align: center;
              margin-bottom: 20px;
            }
            .logo-shell {
              width: 84px;
              height: 84px;
              border-radius: 20px;
              border: 1px solid var(--border);
              background: color-mix(in srgb, var(--panel) 88%, var(--page-bg));
              display: grid;
              place-items: center;
              overflow: hidden;
            }
            .logo,
            .logo-fallback {
              width: 100%;
              height: 100%;
              object-fit: contain;
            }
            .logo-fallback {
              display: grid;
              place-items: center;
              background: linear-gradient(145deg, color-mix(in srgb, var(--accent) 92%, black), color-mix(in srgb, var(--primary) 68%, var(--accent)));
              color: white;
              font-size: 20px;
              font-weight: 700;
              letter-spacing: -0.04em;
            }
            h1 {
              max-width: 14ch;
              font-size: clamp(30px, 5vw, 44px);
              line-height: 1.08;
              letter-spacing: -0.05em;
              font-weight: 700;
            }
            .brand-subtitle {
              max-width: 34rem;
              color: var(--muted);
              font-size: 14px;
              line-height: 1.6;
            }
            .messages {
              display: grid;
              gap: 10px;
              margin: 0 auto 14px;
              width: min(100%, 560px);
            }
            .form-panel {
              width: min(100%, 620px);
              margin: 0 auto;
              padding: clamp(18px, 3vw, 28px);
              border-radius: 20px;
              border: 1px solid var(--border);
              background: var(--panel);
            }
            .panel-intro {
              display: grid;
              gap: 4px;
              margin-bottom: 14px;
            }
            .panel-kicker {
              color: var(--muted);
              font-size: 11px;
              font-weight: 700;
              letter-spacing: 0.08em;
              text-transform: uppercase;
            }
            .panel-intro h2 {
              font-size: 18px;
              line-height: 1.2;
              letter-spacing: -0.03em;
              font-weight: 600;
            }
            .panel-intro p {
              color: var(--muted);
              font-size: 13px;
              line-height: 1.55;
            }
            .invite-summary {
              display: grid;
              gap: 6px;
              padding: 14px;
              border: 1px solid var(--border);
              border-radius: 12px;
              background: color-mix(in srgb, var(--primary) 6%, var(--panel));
              font-size: 14px;
              line-height: 1.45;
            }
            .secret-panel {
              display: grid;
              gap: 6px;
              padding: 12px;
              border: 1px solid var(--border);
              border-radius: 10px;
              background: color-mix(in srgb, var(--primary) 5%, var(--panel));
            }
            .secret-panel span {
              color: var(--muted);
              font-size: 12px;
              font-weight: 700;
              text-transform: uppercase;
            }
            .secret-panel code {
              overflow-wrap: anywhere;
              font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
              font-size: 12px;
              line-height: 1.45;
            }
            .qr-panel {
              display: grid;
              justify-items: center;
              gap: 10px;
              margin-bottom: 14px;
              color: var(--muted);
              font-size: 13px;
              line-height: 1.5;
              text-align: center;
            }
            .qr-code {
              width: min(220px, 100%);
              aspect-ratio: 1;
              border: 1px solid var(--border);
              border-radius: 12px;
              background: #ffffff;
              padding: 10px;
            }
            .manual-setup {
              display: grid;
              gap: 10px;
              margin-bottom: 14px;
            }
            .manual-setup summary {
              color: color-mix(in srgb, var(--primary) 80%, var(--text));
              cursor: pointer;
              font-size: 13px;
              font-weight: 600;
            }
            .manual-setup[open] {
              gap: 10px;
            }
            .invite-summary strong {
              font-size: 15px;
              font-weight: 600;
            }
            .invite-summary span {
              color: var(--muted);
              font-size: 13px;
            }
            .auth-form,
            .organization-list,
            .providers,
            .panel-stack {
              display: grid;
              gap: 14px;
            }
            .field {
              display: grid;
              gap: 6px;
            }
            .field span {
              font-size: 14px;
              font-weight: 500;
            }
            input {
              width: 100%;
              height: 44px;
              padding: 0 14px;
              border-radius: 8px;
              border: 1px solid var(--border-strong);
              background: var(--input-bg);
              color: var(--text);
              font-size: 14px;
              transition: border-color 0.18s ease, box-shadow 0.18s ease, background 0.18s ease;
            }
            input::placeholder {
              color: color-mix(in srgb, var(--muted) 86%, transparent);
            }
            input:focus {
              outline: none;
              border-color: color-mix(in srgb, var(--primary) 32%, white);
              box-shadow: 0 0 0 4px color-mix(in srgb, var(--primary) 18%, transparent);
            }
            .primary-action,
            .secondary-action,
            .provider-link,
            .organization-option,
            .secondary-link {
              transition:
                transform 0.18s ease,
                box-shadow 0.18s ease,
                border-color 0.18s ease,
                background 0.18s ease,
                color 0.18s ease;
            }
            .primary-action {
              width: 100%;
              min-height: 44px;
              border: none;
              border-radius: 8px;
              background: var(--primary);
              color: var(--button-text);
              display: inline-flex;
              align-items: center;
              justify-content: center;
              gap: 8px;
              font-size: 14px;
              font-weight: 500;
              cursor: pointer;
            }
            .secondary-action {
              width: 100%;
              min-height: 44px;
              border: 1px solid var(--border-strong);
              border-radius: 8px;
              background: transparent;
              color: var(--text);
              font-size: 14px;
              font-weight: 500;
              cursor: pointer;
            }
            .primary-action:hover,
            .secondary-action:hover,
            .provider-link:hover,
            .organization-option:hover {
              transform: translateY(-1px);
            }
            .primary-action:focus-visible,
            .secondary-action:focus-visible,
            .provider-link:focus-visible,
            .organization-option:focus-visible,
            .secondary-link:focus-visible {
              outline: 3px solid color-mix(in srgb, var(--primary) 22%, transparent);
              outline-offset: 3px;
            }
            .button-loader,
            .provider-loader,
            .organization-loader {
              opacity: 0;
            }
            .primary-action[data-loading="true"] .button-loader,
            .provider-link[data-loading="true"] .provider-loader,
            .organization-option[data-loading="true"] .organization-loader {
              opacity: 1;
            }
            .primary-action[data-loading="true"],
            .provider-link[data-loading="true"],
            .organization-option[data-loading="true"] {
              pointer-events: none;
            }
            .section-divider {
              display: flex;
              align-items: center;
              gap: 14px;
              color: var(--muted);
              font-size: 12px;
              font-weight: 500;
              line-height: 1;
              margin: 6px 0 2px;
            }
            .section-divider::before,
            .section-divider::after {
              content: "";
              flex: 1;
              height: 1px;
              background: var(--border);
            }
            .provider-link,
            .organization-option {
              width: 100%;
              min-height: 44px;
              padding: 0 14px;
              border-radius: 8px;
              border: 1px solid var(--border-strong);
              background: var(--panel);
              color: var(--text);
              display: flex;
              align-items: center;
              justify-content: center;
              gap: 10px;
              text-decoration: none;
            }
            .provider-link[data-loading="true"] .provider-badge,
            .provider-link[data-loading="true"] .provider-label,
            .organization-option[data-loading="true"] .organization-copy {
              opacity: 0.55;
            }
            .provider-badge {
              width: 32px;
              height: 32px;
              border-radius: 10px;
              border: 1px solid var(--border);
              background: var(--input);
              display: grid;
              place-items: center;
              overflow: hidden;
            }
            .provider-badge,
            .provider-loader,
            .organization-loader {
              flex: none;
            }
            .provider-logo {
              width: 20px;
              height: 20px;
              display: block;
              object-fit: contain;
            }
            .provider-generic {
              width: 100%;
              height: 100%;
              background: color-mix(in srgb, var(--primary) 16%, var(--input));
              display: grid;
              place-items: center;
              font-size: 10px;
              font-weight: 700;
              letter-spacing: 0.04em;
              color: color-mix(in srgb, var(--primary) 64%, var(--text));
            }
            .provider-label {
              font-size: 14px;
              font-weight: 500;
              line-height: 1.25;
            }
            .footer-prompt,
            .footer-links {
              margin-top: 8px;
              color: var(--muted);
              font-size: 14px;
              line-height: 1.6;
              text-align: center;
            }
            .secondary-link {
              color: color-mix(in srgb, var(--primary) 80%, var(--text));
              font-weight: 600;
              text-decoration: none;
            }
            .organization-list {
              gap: 12px;
            }
            .organization-option {
              justify-content: space-between;
              text-align: left;
              cursor: pointer;
            }
            .organization-copy {
              display: grid;
              gap: 4px;
              min-width: 0;
            }
            .organization-copy strong {
              font-size: 14px;
              line-height: 1.35;
              font-weight: 500;
            }
            .organization-copy small {
              color: var(--muted);
              font-size: 12px;
              line-height: 1.45;
            }
            .flow-status {
              display: flex;
              align-items: center;
              justify-content: center;
              gap: 8px;
              color: var(--muted);
              font-size: 12px;
              line-height: 1.45;
              opacity: 0;
              transform: translateY(-4px);
              transition: opacity 0.18s ease, transform 0.18s ease;
            }
            .flow-status[data-visible="true"] {
              opacity: 1;
              transform: translateY(0);
            }
            .loader {
              display: inline-block;
              border-radius: 999px;
              border: 2px solid currentColor;
              border-right-color: transparent;
              animation: spin 0.72s linear infinite;
            }
            .loader-sm {
              width: 14px;
              height: 14px;
            }
            .callout {
              display: flex;
              align-items: flex-start;
              gap: 10px;
              padding: 12px 14px;
              border-radius: 10px;
              border: 1px solid var(--border);
              font-size: 13px;
              line-height: 1.55;
            }
            .callout-icon {
              width: 20px;
              height: 20px;
              border-radius: 999px;
              display: grid;
              place-items: center;
              font-size: 11px;
              font-weight: 700;
              flex: none;
            }
            .callout.error {
              border-color: #fecaca;
              background: #fef2f2;
              color: #991b1b;
            }
            .callout.error .callout-icon {
              background: rgba(220, 38, 38, 0.12);
            }
            .callout.info {
              border-color: #bfdbfe;
              background: #eff6ff;
              color: #1d4ed8;
            }
            .callout.info .callout-icon {
              background: rgba(37, 99, 235, 0.12);
            }
            .state-card {
              display: flex;
              align-items: center;
              gap: 12px;
              padding: 16px;
              border-radius: 12px;
              border: 1px solid var(--border);
              background: var(--panel);
            }
            .state-icon {
              width: 36px;
              height: 36px;
              border-radius: 12px;
              border: 1px solid color-mix(in srgb, var(--primary) 20%, var(--border));
              background: color-mix(in srgb, var(--primary) 12%, var(--panel));
              display: grid;
              place-items: center;
              font-size: 11px;
              font-weight: 700;
              color: color-mix(in srgb, var(--primary) 80%, var(--text));
              flex: none;
            }
            .state-copy {
              display: grid;
              gap: 4px;
            }
            .state-copy strong {
              font-size: 15px;
            }
            .state-copy p {
              color: var(--muted);
              font-size: 13px;
              line-height: 1.55;
            }
            @keyframes spin {
              from { transform: rotate(0deg); }
              to { transform: rotate(360deg); }
            }
            @media (max-width: 640px) {
              .page-shell {
                padding: 18px 10px;
              }
              .auth-frame {
                padding: 20px 14px;
                border-radius: 24px;
              }
              .logo-shell {
                width: 72px;
                height: 72px;
                border-radius: 18px;
              }
              .form-panel {
                padding: 16px 14px;
                border-radius: 16px;
              }
              h1 {
                font-size: 26px;
              }
            }
            @media (prefers-reduced-motion: reduce) {
              *,
              *::before,
              *::after {
                animation: none !important;
                transition: none !important;
                scroll-behavior: auto !important;
              }
            }
          </style>
        </head>
        <body>
          <div class="page-shell">
            <main class="auth-shell {{(isStackedLayout ? "stacked" : "split")}}">
              <section class="auth-frame">
                <div class="brand-header">
                  <div class="logo-shell">{{logoMarkup}}</div>
                  <h1>{{Html(title)}}</h1>
                  {{subtitleMarkup}}
                </div>
                <div class="messages">
                  {{errorMarkup}}
                  {{infoMarkup}}
                </div>
                <section class="form-panel">
                  <div class="panel-stack">
                    {{content}}
                  </div>
                </section>
              </section>
            </main>
          </div>
          <script {{SqlOSCspNonce.Attribute}}>{{RenderClientScript()}}</script>
        </body>
        </html>
        """;
    }

    private static string RenderPrimaryAction(string label, string loadingLabel)
        => $$"""
            <button class="primary-action" type="submit" data-loading-label="{{Html(loadingLabel)}}">
              <span class="button-label">{{Html(label)}}</span>
              <span class="loader loader-sm button-loader" aria-hidden="true"></span>
            </button>
            """;

    private static string RenderPanelIntro(string kicker, string copy)
        => $$"""
            <div class="panel-intro">
              <span class="panel-kicker">{{Html(kicker)}}</span>
              <p>{{Html(copy)}}</p>
            </div>
            """;

    private static string RenderInvitationSummary(SqlOSEmailInvitationResult? invitation)
    {
        if (invitation == null)
        {
            return string.Empty;
        }

        return $$"""
            <div class="invite-summary">
              <strong>{{Html(invitation.OrganizationName)}}</strong>
              <span>{{Html(invitation.Email)}} invited as {{Html(invitation.Role)}}.</span>
              <span>Expires {{Html(invitation.ExpiresAt.ToString("g", CultureInfo.InvariantCulture))}} UTC.</span>
            </div>
            """;
    }

    private static string RenderConsentSummary(SqlOSAuthPageViewModel model)
    {
        var scopes = model.ConsentScopes ?? Array.Empty<SqlOSConsentScopeDisplay>();
        var scopeMarkup = scopes.Count == 0
            ? "<span>Default access</span>"
            : string.Join("", scopes.Select(scope =>
            {
                var description = string.IsNullOrWhiteSpace(scope.Description)
                    ? string.Empty
                    : $" — {Html(scope.Description!)}";
                return $"<span class=\"consent-scope\">{Html(scope.DisplayName)}{description}</span>";
            }));

        return $$"""
            <div class="invite-summary">
              <strong>{{Html(model.ClientName ?? "Unknown application")}}</strong>
              <span>This application will be able to:</span>
              {{scopeMarkup}}
            </div>
            """;
    }

    private static string RenderDeviceSummary(SqlOSDeviceAuthorizationResolveResult? deviceAuthorization)
    {
        if (deviceAuthorization == null)
        {
            return string.Empty;
        }

        var scope = string.IsNullOrWhiteSpace(deviceAuthorization.Scope)
            ? "Default access"
            : deviceAuthorization.Scope;
        var resource = string.IsNullOrWhiteSpace(deviceAuthorization.Resource)
            ? string.Empty
            : $"<span>Resource: {Html(deviceAuthorization.Resource)}</span>";

        return $$"""
            <div class="invite-summary">
              <strong>{{Html(deviceAuthorization.ClientName)}}</strong>
              <span>Code {{Html(deviceAuthorization.UserCode)}}.</span>
              <span>Scopes: {{Html(scope)}}.</span>
              {{resource}}
              <span>Expires {{Html(deviceAuthorization.ExpiresAt.ToString("g", CultureInfo.InvariantCulture))}} UTC.</span>
            </div>
            """;
    }

    private static string RenderProvidersSection(SqlOSAuthPageViewModel model)
    {
        if (model.Providers.Count == 0)
        {
            return string.Empty;
        }

        var providersMarkup = string.Join("", model.Providers.Select(RenderProviderLink));
        return $$"""
            <div class="section-divider">OR</div>
            <div class="providers">{{providersMarkup}}</div>
            """;
    }

    private static string RenderProviderLink(SqlOSAuthPageProviderLink provider)
    {
        var providerName = Html(provider.DisplayName);
        return $$"""
            <a class="provider-link js-loading-link" href="{{Html(provider.Url)}}" data-loading-label="Connecting to {{providerName}}">
              <span class="provider-badge">{{RenderProviderBadge(provider)}}</span>
              <span class="provider-label">Continue with {{providerName}}</span>
              <span class="loader loader-sm provider-loader" aria-hidden="true"></span>
            </a>
            """;
    }

    private static string RenderOrganizationOptions(IReadOnlyList<SqlOSOrganizationOption> organizations)
    {
        if (organizations.Count == 0)
        {
            return "<p class=\"footer-links\">No organizations were available for this sign-in attempt.</p>";
        }

        return string.Join("", organizations.Select(option =>
        {
            var detail = string.IsNullOrWhiteSpace(option.Slug)
                ? option.Role
                : $"{option.Slug} · {option.Role}";

            return $$"""
                <button class="organization-option" type="submit" name="organizationId" value="{{Html(option.Id)}}" data-loading-label="Opening workspace">
                  <span class="organization-copy">
                    <strong>{{Html(option.Name)}}</strong>
                    <small>{{Html(detail)}}</small>
                  </span>
                  <span class="loader loader-sm organization-loader" aria-hidden="true"></span>
                </button>
                """;
        }));
    }

    private static string RenderFooterPrompt(string prompt, string linkMarkup)
        => string.IsNullOrWhiteSpace(linkMarkup)
            ? string.Empty
            : $"<p class=\"footer-prompt\">{Html(prompt)} {linkMarkup}</p>";

    private static string RenderFooterLinks(params string[] links)
    {
        var activeLinks = links
            .Where(link => !string.IsNullOrWhiteSpace(link))
            .ToArray();

        return activeLinks.Length == 0
            ? string.Empty
            : $"<div class=\"footer-links\">{string.Join("", activeLinks)}</div>";
    }

    private static string BuildCallout(string kind, string? message, string? warningCode = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var icon = string.Equals(kind, "error", StringComparison.OrdinalIgnoreCase) ? "!" : "i";
        var warningAttr = string.IsNullOrWhiteSpace(warningCode)
            ? string.Empty
            : $" data-omitted-openid-warning=\"{Html(warningCode)}\"";
        return $$"""
            <div class="callout {{Html(kind)}}"{{warningAttr}}>
              <span class="callout-icon">{{icon}}</span>
              <span>{{Html(message)}}</span>
            </div>
            """;
    }

    private static string RenderProviderBadge(SqlOSAuthPageProviderLink provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.LogoDataUrl))
        {
            return $"<img class=\"provider-logo\" src=\"{Html(provider.LogoDataUrl)}\" alt=\"\" aria-hidden=\"true\" />";
        }

        var monogram = GetMonogram(provider.DisplayName);
        return $"<span class=\"provider-generic\">{Html(monogram)}</span>";
    }

    private static string RenderClientScript()
        => """
            (() => {
              let activeSubmitter = null;

              document.addEventListener("click", event => {
                const submitter = event.target.closest('button[type="submit"]');
                if (submitter) {
                  activeSubmitter = submitter;
                  return;
                }

                const loadingLink = event.target.closest(".js-loading-link");
                if (!loadingLink) {
                  return;
                }

                if (loadingLink.dataset.loading === "true") {
                  event.preventDefault();
                  return;
                }

                loadingLink.dataset.loading = "true";
                loadingLink.setAttribute("aria-disabled", "true");
              });

              document.querySelectorAll(".auth-form").forEach(form => {
                form.addEventListener("submit", event => {
                  if (form.dataset.loading === "true") {
                    event.preventDefault();
                    return;
                  }

                  form.dataset.loading = "true";
                  const submitter = event.submitter || activeSubmitter || form.querySelector('button[type="submit"]');

                  if (submitter) {
                    submitter.dataset.loading = "true";

                    if (submitter instanceof HTMLButtonElement) {
                      submitter.disabled = true;
                    }

                    const label = submitter.querySelector(".button-label");
                    if (label && submitter.dataset.loadingLabel) {
                      label.textContent = submitter.dataset.loadingLabel;
                    }
                  }

                  const flowStatus = form.querySelector(".flow-status");
                  if (flowStatus) {
                    flowStatus.hidden = false;
                    window.requestAnimationFrame(() => {
                      flowStatus.dataset.visible = "true";
                    });
                  }
                });
              });
            })();
            """;

    private static string GetMonogram(string value)
    {
        var parts = value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => new string(part.Where(char.IsLetterOrDigit).ToArray()))
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        if (parts.Length == 0)
        {
            return "ID";
        }

        if (parts.Length == 1)
        {
            var token = parts[0].ToUpperInvariant();
            return token.Length == 1 ? token : token[..2];
        }

        return string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
    }

    private static string NormalizeMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "login";
        }

        var normalized = mode.Trim().ToLowerInvariant();
        return normalized is "device" or "device-approve" or "device-approved"
            ? normalized
            : normalized;
    }

    private static string BuildRequestQuery(string? requestId, string? invitationToken, string? deviceUserCode)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            query.Add($"request={Uri.EscapeDataString(requestId)}");
        }

        if (!string.IsNullOrWhiteSpace(invitationToken))
        {
            query.Add($"invitationToken={Uri.EscapeDataString(invitationToken)}");
        }

        if (!string.IsNullOrWhiteSpace(deviceUserCode))
        {
            query.Add($"deviceUserCode={Uri.EscapeDataString(deviceUserCode)}");
        }

        return query.Count == 0 ? string.Empty : $"?{string.Join("&", query)}";
    }

    private static bool SupportsCredentialType(IEnumerable<string>? enabledCredentialTypes, string credentialType)
        => (enabledCredentialTypes ?? Array.Empty<string>())
            .Any(value => string.Equals(value, credentialType, StringComparison.OrdinalIgnoreCase));

    private static string AuthPath(SqlOSAuthPageViewModel model, string path, string? requestId = null)
    {
        var basePath = model.BasePath.TrimEnd('/');
        var normalizedPath = path.StartsWith('/') ? path : $"/{path}";
        return $"{basePath}{normalizedPath}{BuildRequestQuery(requestId, model.InvitationToken, model.DeviceUserCode)}";
    }

    private static string AuthPathWithQuery(
        SqlOSAuthPageViewModel model,
        string path,
        string? requestId = null,
        params (string Key, string? Value)[] queryItems)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            query.Add($"request={Uri.EscapeDataString(requestId)}");
        }

        if (!string.IsNullOrWhiteSpace(model.InvitationToken))
        {
            query.Add($"invitationToken={Uri.EscapeDataString(model.InvitationToken)}");
        }

        if (!string.IsNullOrWhiteSpace(model.DeviceUserCode))
        {
            query.Add($"deviceUserCode={Uri.EscapeDataString(model.DeviceUserCode)}");
        }

        foreach (var (key, value) in queryItems)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            query.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        }

        var basePath = AuthPath(model, path);
        return query.Count == 0
            ? basePath
            : $"{basePath.Split('?', 2)[0]}?{string.Join("&", query)}";
    }

    private static string Css(string? value, string fallback)
        => SqlOSCssColor.Render(value, fallback);

    private static string Html(string value) => WebUtility.HtmlEncode(value);

    private static bool IsDarkColor(string? value)
        => SqlOSCssColor.TryGetRgb(value, out var red, out var green, out var blue) &&
           RelativeLuminance(red, green, blue) < 0.42;

    private static string GetContrastingTextColor(string value)
        => SqlOSCssColor.TryGetRgb(value, out var red, out var green, out var blue) &&
           RelativeLuminance(red, green, blue) > 0.52
            ? "#111827"
            : "#ffffff";

    private static double RelativeLuminance(int red, int green, int blue)
    {
        static double Channel(double value)
            => value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

        var r = Channel(red / 255d);
        var g = Channel(green / 255d);
        var b = Channel(blue / 255d);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }
}

public sealed record SqlOSAuthPageViewModel(
    string Mode,
    SqlOSAuthPageSettingsDto Settings,
    string BasePath,
    string? AuthorizationRequestId,
    string? Email,
    string? DisplayName,
    string? Error,
    string? Info,
    string? PendingToken,
    IReadOnlyList<SqlOSOrganizationOption> OrganizationSelection,
    IReadOnlyList<SqlOSAuthPageProviderLink> Providers,
    string? ChallengeToken = null,
    string? SignupToken = null,
    string? InvitationToken = null,
    SqlOSEmailInvitationResult? Invitation = null,
    string? DeviceUserCode = null,
    SqlOSDeviceAuthorizationResolveResult? DeviceAuthorization = null,
    string? PhoneNumber = null,
    string? MfaToken = null,
    IReadOnlyList<string>? MfaMethods = null,
    string? EnrollmentToken = null,
    string? TotpSecret = null,
    string? TotpProvisioningUri = null,
    string? TotpQrCodeDataUrl = null,
    bool OmittedOpenId = false,
    string? ConsentToken = null,
    string? ClientName = null,
    IReadOnlyList<SqlOSConsentScopeDisplay>? ConsentScopes = null);

public sealed record SqlOSAuthPageProviderLink(string ConnectionId, string DisplayName, string Url, string? LogoDataUrl = null);
