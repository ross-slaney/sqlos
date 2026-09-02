"use client";

import { useEffect, useState } from "react";
import { useSearchParams } from "next/navigation";
import Link from "next/link";
import { useHeadlessAuth } from "@sqlos/headless/react";
import { startHostedSqlOSSignIn } from "@/components/sqlos-hosted-sign-in";
import { getExampleAuthServerUrl, getExampleClientId } from "@/lib/sqlos-config";
import type { HeadlessProvider, HeadlessView } from "@sqlos/headless";

type ReferralOption = {
  value: string;
  label: string;
};

const referralOptions: ReferralOption[] = [
  { value: "docs", label: "SqlOS docs or examples" },
  { value: "mcp", label: "MCP integration work" },
  { value: "friend", label: "Recommendation from a teammate" },
  { value: "review", label: "Build vs. buy auth evaluation" },
];

function buildDisplayName(firstName: string, lastName: string, fallbackEmail: string) {
  const combined = `${firstName} ${lastName}`.trim();
  return combined || fallbackEmail.trim() || "Example User";
}

function getProviderMonogram(displayName: string) {
  const parts = displayName
    .trim()
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2);

  if (parts.length === 0) {
    return "?";
  }

  return parts.map((part) => part.charAt(0).toUpperCase()).join("");
}

const IMAGE_LOGIN = "https://images.unsplash.com/photo-1604719312566-8912e9227c6a?w=1200&q=80&auto=format";
const IMAGE_SIGNUP = "https://images.unsplash.com/photo-1556740758-90de374c12ad?w=1200&q=80&auto=format";

export function SqlOSHeadlessAuthPanel() {
  const searchParams = useSearchParams();
  const requestId = searchParams.get("request");
  const initialView = searchParams.get("view") || "login";
  const initialEmail = searchParams.get("email") || "";
  const initialDisplayName = searchParams.get("displayName") || "";
  const initialResetToken = searchParams.get("token") || "";
  const nextPath = searchParams.get("next") || "/retail";

  const {
    flow,
    status,
    view: flowView,
    viewModel,
    error,
    fieldErrors,
    authorization,
    redirectUrl,
  } = useHeadlessAuth({
    issuer: getExampleAuthServerUrl(),
    clientId: getExampleClientId(),
    redirectUri: typeof window === "undefined"
      ? "http://localhost:3010/api/auth/callback/sqlos"
      : `${window.location.origin}/api/auth/callback/sqlos`,
    credentials: "include",
  });

  const [formView, setFormView] = useState<HeadlessView | null>(
    initialResetToken ? "password-reset" : null,
  );
  const [notice, setNotice] = useState<string | null>(null);
  const [confirmMismatch, setConfirmMismatch] = useState<string | null>(null);

  const [email, setEmail] = useState(initialEmail);
  const [phoneNumber, setPhoneNumber] = useState("");
  const [password, setPassword] = useState("");
  const [otpCode, setOtpCode] = useState("");
  const [organizationName, setOrganizationName] = useState("");
  const [firstName, setFirstName] = useState("");
  const [lastName, setLastName] = useState("");
  const [referralSource, setReferralSource] = useState("");
  const [mfaCode, setMfaCode] = useState("");
  const [mfaEnrollmentCode, setMfaEnrollmentCode] = useState("");
  const [startedMfaEnrollmentToken, setStartedMfaEnrollmentToken] = useState<string | null>(null);
  const [resetEmail, setResetEmail] = useState(initialEmail);
  const [resetToken, setResetToken] = useState(initialResetToken);
  const [newPassword, setNewPassword] = useState("");
  const [confirmNewPassword, setConfirmNewPassword] = useState("");

  const loading = status === "loading";
  const view = formView ?? flowView ?? initialView;

  useEffect(() => {
    if (status === "redirect" && redirectUrl) {
      window.location.assign(redirectUrl);
    }
  }, [status, redirectUrl]);

  useEffect(() => {
    if (!requestId) return;
    void flow.resume(window.location);
  }, [flow, requestId]);

  useEffect(() => {
    if (initialResetToken) {
      setResetToken(initialResetToken);
    }
  }, [initialResetToken]);

  useEffect(() => {
    // Drop client-only form switches only when the server advances the view.
    setFormView(null);
  }, [viewModel?.view]);

  useEffect(() => {
    if (viewModel?.email) {
      setEmail(viewModel.email);
      setResetEmail(viewModel.email);
    }
  }, [viewModel?.email]);

  useEffect(() => {
    if (viewModel?.phoneNumber) {
      setPhoneNumber(viewModel.phoneNumber);
    }
  }, [viewModel?.phoneNumber]);

  useEffect(() => {
    if (viewModel?.challengeToken) {
      setOtpCode("");
    }
  }, [viewModel?.challengeToken]);

  useEffect(() => {
    if (viewModel?.mfaToken) {
      setMfaCode("");
    }
  }, [viewModel?.mfaToken]);

  useEffect(() => {
    if (viewModel?.totpEnrollment) {
      setMfaEnrollmentCode("");
    }
  }, [viewModel?.totpEnrollment]);

  useEffect(() => {
    if (viewModel?.displayName && !firstName && !lastName && initialDisplayName) {
      const [first = "", ...rest] = viewModel.displayName.split(" ");
      setFirstName(first);
      setLastName(rest.join(" "));
    }
  }, [firstName, initialDisplayName, lastName, viewModel?.displayName]);

  useEffect(() => {
    if (view !== "mfa-enroll" || !viewModel?.mfaToken || viewModel.totpEnrollment) return;
    if (startedMfaEnrollmentToken === viewModel.mfaToken) return;
    setStartedMfaEnrollmentToken(viewModel.mfaToken);
    if (status === "idle") return;
    void flow.mfa.totp.enrollStart({ displayName: "Authenticator app" });
  }, [flow, startedMfaEnrollmentToken, status, view, viewModel?.mfaToken, viewModel?.totpEnrollment]);

  const onStartMfaEnrollment = () => {
    if (status === "idle") return;
    void flow.mfa.totp.enrollStart({ displayName: "Authenticator app" });
  };

  const onIdentify = (event: React.FormEvent) => {
    event.preventDefault();
    setNotice(null);
    void flow.identify({ email });
  };

  const onLogin = (event: React.FormEvent) => {
    event.preventDefault();
    setNotice(null);
    void flow.password.login({ email, password });
  };

  const onRequestPasswordReset = (event: React.FormEvent) => {
    event.preventDefault();
    setNotice(null);
    void flow.password.forgot({ email: (resetEmail || email).trim() });
  };

  const onResetPassword = (event: React.FormEvent) => {
    event.preventDefault();
    setNotice(null);
    if (newPassword !== confirmNewPassword) {
      setConfirmMismatch("Passwords do not match.");
      return;
    }
    setConfirmMismatch(null);
    void flow.password.reset({ token: resetToken, newPassword }).then((next) => {
      if (next === "error") return;
      setPassword("");
      setNewPassword("");
      setConfirmNewPassword("");
      setNotice("Your password has been reset. Sign in with your new password.");
      setFormView("login");
    });
  };

  const onRequestEmailOtp = (event: React.FormEvent) => {
    event.preventDefault();
    setNotice(null);
    void flow.emailOtp.start({ email });
  };

  const onRequestMagicLink = (event: React.FormEvent) => {
    event.preventDefault();
    setNotice(null);
    void flow.magicLink.start({ email });
  };

  const onVerifyEmailOtp = (event: React.FormEvent) => {
    event.preventDefault();
    setNotice(null);
    void flow.emailOtp.verify({ code: otpCode });
  };

  const onRequestPhoneOtp = (event: React.FormEvent) => {
    event.preventDefault();
    setNotice(null);
    void flow.phoneOtp.start({ phoneNumber });
  };

  const onVerifyPhoneOtp = (event: React.FormEvent) => {
    event.preventDefault();
    setNotice(null);
    void flow.phoneOtp.verify({ code: otpCode });
  };

  const onSignup = (event: React.FormEvent) => {
    event.preventDefault();
    setNotice(null);
    void flow.signup({
      displayName: buildDisplayName(firstName, lastName, email),
      email,
      password,
      organizationName,
      customFields: { referralSource, firstName, lastName },
    });
  };

  const onRequestPhoneOtpSignup = (event: React.FormEvent) => {
    event.preventDefault();
    setNotice(null);
    void flow.phoneOtp.signupStart({
      displayName: buildDisplayName(firstName, lastName, phoneNumber),
      phoneNumber,
      organizationName,
      customFields: { referralSource, firstName, lastName },
    });
  };

  const onVerifyPhoneOtpSignup = (event: React.FormEvent) => {
    event.preventDefault();
    setNotice(null);
    void flow.phoneOtp.signupVerify({ code: otpCode });
  };

  const onProviderStart = (connectionId: string) => {
    setNotice(null);
    void flow.provider.start({ connectionId, email: email || undefined });
  };

  const onSelectOrganization = (organizationId: string) => {
    setNotice(null);
    void flow.organization.select({ organizationId });
  };

  const onDecideConsent = (approve: boolean) => {
    setNotice(null);
    void (approve ? flow.consent.approve() : flow.consent.deny());
  };

  const onVerifyMfa = (event: React.FormEvent) => {
    event.preventDefault();
    setNotice(null);
    void flow.mfa.verify({ code: mfaCode });
  };

  const onVerifyMfaEnrollment = (event: React.FormEvent) => {
    event.preventDefault();
    setNotice(null);
    void flow.mfa.totp.enrollVerify({ code: mfaEnrollmentCode });
  };

  const isSignup = view === "signup" || view === "phone-otp-signup" || view === "phone-otp-signup-verify";
  const isMfa = view === "mfa" || view === "mfa-enroll";
  const isRecovery = view === "forgot-password" || view === "forgot-password-sent" || view === "password-reset";
  const showProviderButtons = (view === "login" || view === "signup") && (viewModel?.providers?.length ?? 0) > 0;
  const supportsPassword = !!viewModel?.settings?.localPasswordRuntimeEnabled
    && (viewModel?.settings?.enabledCredentialTypes ?? []).includes("password");
  const supportsEmailOtp = !!viewModel?.settings?.emailOtpRuntimeConfigured
    && (viewModel?.settings?.enabledCredentialTypes ?? []).includes("email_otp");
  const supportsMagicLink = !!viewModel?.settings?.magicLinkRuntimeConfigured
    && (viewModel?.settings?.enabledCredentialTypes ?? []).includes("magic_link");
  const supportsPhoneOtp = !!viewModel?.settings?.phoneOtpRuntimeConfigured
    && (viewModel?.settings?.enabledCredentialTypes ?? []).includes("phone_otp");

  const headline = isSignup
    ? "Start your free trial"
    : view === "consent"
      ? "Authorize access"
    : view === "organization"
      ? "Choose workspace"
      : view === "mfa"
        ? "Two-step verification"
        : view === "mfa-enroll"
          ? "Add authenticator app"
          : view === "forgot-password"
            ? "Recover account"
            : view === "forgot-password-sent"
              ? "Check your email"
              : view === "password-reset"
                ? "Reset password"
                : "Welcome back";
  const subtitle = isSignup
    ? "Create your account and start managing retail operations in minutes."
    : view === "consent"
      ? `${viewModel?.clientName ?? "The application"} is asking to access your account.`
    : view === "organization"
      ? "Select the organization you'd like to sign in to."
      : view === "mfa"
        ? "Enter an authenticator code or one of your recovery codes."
        : view === "mfa-enroll"
          ? "Set up an authenticator app before continuing."
          : view === "forgot-password"
            ? "Enter your email and we'll send recovery instructions if the account can be reset."
            : view === "forgot-password-sent"
              ? "Use the link in your email to choose a new password."
              : view === "password-reset"
                ? "Choose a new password for your account."
                : "Sign in to your Northwind Retail account.";
  const testimonialQuote = isSignup
    ? "Setting up took less than five minutes. We had our entire team onboarded before lunch."
    : isMfa || isRecovery
      ? "I can keep access secure without slowing down the team."
    : "I love that I can see exactly my stores. No noise, no clutter — just the data I need.";
  const testimonialName = isSignup ? "Marcus Rivera" : isMfa || isRecovery ? "Avery Chen" : "Priya Sharma";
  const testimonialRole = isSignup ? "Head of Retail Ops, FreshMart" : isMfa || isRecovery ? "IT Manager, Northwind Retail" : "Store Manager, Target #100";

  return (
    <div className="ha">
      {/* ── Left: image + branding ── */}
      <div className="ha-left" style={{ backgroundImage: `url(${isSignup ? IMAGE_SIGNUP : IMAGE_LOGIN})` }}>
        <div className="ha-left-overlay" />
        <div className="ha-left-content">
          <Link href="/" className="ha-brand">
            <div className="ha-brand-icon">N</div>
            <span>Northwind Retail</span>
          </Link>

          <div className="ha-left-bottom">
            <blockquote className="ha-quote">
              &ldquo;{testimonialQuote}&rdquo;
            </blockquote>
            <div className="ha-quote-author">
              <strong>{testimonialName}</strong>
              <span>{testimonialRole}</span>
            </div>

            <div className="ha-badge-row">
              <span className="ha-tech-badge">Headless Auth</span>
              <span className="ha-tech-badge">OAuth 2.0 + PKCE</span>
              <span className="ha-tech-badge">SqlOS</span>
            </div>
          </div>
        </div>
      </div>

      {/* ── Right: form ── */}
      <div className="ha-right">
        <div className="ha-form-wrapper">
          <div className="ha-form-header">
            <h1>{headline}</h1>
            <p>{subtitle}</p>
          </div>

          {error && <div className="ha-error">{error}</div>}
          {(notice || viewModel?.info) && <div className="ha-success">{notice || viewModel?.info}</div>}

          {!requestId && view === "password-reset" ? (
            <form className="ha-form" onSubmit={onResetPassword}>
              <div className="ha-field">
                <label htmlFor="ha-reset-token">Reset token</label>
                <input id="ha-reset-token" type="text" value={resetToken} onChange={(e) => setResetToken(e.target.value)} required />
              </div>
              <div className="ha-field">
                <label htmlFor="ha-new-password">New password</label>
                <input id="ha-new-password" type="password" value={newPassword} onChange={(e) => setNewPassword(e.target.value)} autoComplete="new-password" required autoFocus />
              </div>
              <div className="ha-field">
                <label htmlFor="ha-confirm-password">Confirm password</label>
                <input id="ha-confirm-password" type="password" value={confirmNewPassword} onChange={(e) => setConfirmNewPassword(e.target.value)} autoComplete="new-password" required />
                {confirmMismatch && <p className="ha-field-error">{confirmMismatch}</p>}
              </div>
              <button type="submit" className="ha-submit" disabled={loading}>
                {loading ? "Resetting..." : "Reset password"}
              </button>
            </form>
          ) : !requestId && view === "forgot-password" ? (
            <form className="ha-form" onSubmit={onRequestPasswordReset}>
              <div className="ha-field">
                <label htmlFor="ha-forgot-email-standalone">Email address</label>
                <input id="ha-forgot-email-standalone" type="email" value={resetEmail} onChange={(e) => setResetEmail(e.target.value)} placeholder="you@company.com" required autoFocus />
              </div>
              <button type="submit" className="ha-submit" disabled={loading}>
                {loading ? "Sending..." : "Send recovery email"}
              </button>
            </form>
          ) : !requestId ? (
            <HeadlessFlowStarter initialView={isSignup ? "signup" : "login"} nextPath={nextPath} />
          ) : (
            <>
              {view === "login" && (
                <form className="ha-form" onSubmit={onIdentify}>
                  <div className="ha-field">
                    <label htmlFor="ha-email">Email address</label>
                    <input id="ha-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} placeholder="you@company.com" required autoFocus />
                    {fieldErrors.email && <p className="ha-field-error">{fieldErrors.email}</p>}
                  </div>
                  <button type="submit" className="ha-submit" disabled={loading}>
                    {loading ? "Checking..." : "Continue"}
                  </button>
                  <div className="ha-alt">
                    Don&apos;t have an account?{" "}
                    <button type="button" className="ha-link-btn" onClick={() => setFormView("signup")}>Sign up</button>
                    {supportsMagicLink && <button type="button" className="ha-link-btn" onClick={() => setFormView("magic-link")}>Email me a link</button>}
                    {supportsPhoneOtp && <button type="button" className="ha-link-btn" onClick={() => setFormView("phone-otp")}>Use phone instead</button>}
                  </div>
                </form>
              )}

              {view === "password" && (
                <form className="ha-form" onSubmit={onLogin}>
                  <div className="ha-field">
                    <label htmlFor="ha-pw-email">Email</label>
                    <input id="ha-pw-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
                  </div>
                  <div className="ha-field">
                    <label htmlFor="ha-pw">Password</label>
                    <input id="ha-pw" type="password" value={password} onChange={(e) => setPassword(e.target.value)} placeholder="Enter your password" required autoFocus />
                    {fieldErrors.password && <p className="ha-field-error">{fieldErrors.password}</p>}
                  </div>
                  <button type="submit" className="ha-submit" disabled={loading}>
                    {loading ? "Signing in..." : "Sign in"}
                  </button>
                  <div className="ha-alt">
                    <button type="button" className="ha-link-btn" onClick={() => setFormView("login")}>Use a different email</button>
                    <button type="button" className="ha-link-btn" onClick={() => { setResetEmail(email); setFormView("forgot-password"); }}>Forgot password?</button>
                    {supportsEmailOtp && <button type="button" className="ha-link-btn" onClick={() => setFormView("email-otp")}>Email me a code instead</button>}
                    {supportsMagicLink && <button type="button" className="ha-link-btn" onClick={() => setFormView("magic-link")}>Email me a link instead</button>}
                    {supportsPhoneOtp && <button type="button" className="ha-link-btn" onClick={() => setFormView("phone-otp")}>Text me a code instead</button>}
                  </div>
                </form>
              )}

              {view === "forgot-password" && (
                <form className="ha-form" onSubmit={onRequestPasswordReset}>
                  <div className="ha-field">
                    <label htmlFor="ha-forgot-email">Email address</label>
                    <input id="ha-forgot-email" type="email" value={resetEmail || email} onChange={(e) => setResetEmail(e.target.value)} placeholder="you@company.com" required autoFocus />
                  </div>
                  <button type="submit" className="ha-submit" disabled={loading}>
                    {loading ? "Sending..." : "Send recovery email"}
                  </button>
                  <div className="ha-alt">
                    <button type="button" className="ha-link-btn" onClick={() => setFormView(email ? "password" : "login")}>Back to sign in</button>
                  </div>
                </form>
              )}

              {view === "forgot-password-sent" && (
                <div className="ha-form">
                  <button type="button" className="ha-submit" onClick={() => setFormView("login")}>
                    Back to sign in
                  </button>
                </div>
              )}

              {view === "password-reset" && (
                <form className="ha-form" onSubmit={onResetPassword}>
                  <div className="ha-field">
                    <label htmlFor="ha-reset-token-flow">Reset token</label>
                    <input id="ha-reset-token-flow" type="text" value={resetToken} onChange={(e) => setResetToken(e.target.value)} required />
                  </div>
                  <div className="ha-field">
                    <label htmlFor="ha-new-password-flow">New password</label>
                    <input id="ha-new-password-flow" type="password" value={newPassword} onChange={(e) => setNewPassword(e.target.value)} autoComplete="new-password" required autoFocus />
                  </div>
                  <div className="ha-field">
                    <label htmlFor="ha-confirm-password-flow">Confirm password</label>
                    <input id="ha-confirm-password-flow" type="password" value={confirmNewPassword} onChange={(e) => setConfirmNewPassword(e.target.value)} autoComplete="new-password" required />
                    {confirmMismatch && <p className="ha-field-error">{confirmMismatch}</p>}
                  </div>
                  <button type="submit" className="ha-submit" disabled={loading}>
                    {loading ? "Resetting..." : "Reset password"}
                  </button>
                </form>
              )}

              {view === "email-otp" && (
                <form className="ha-form" onSubmit={onRequestEmailOtp}>
                  <div className="ha-field">
                    <label htmlFor="ha-otp-email">Email</label>
                    <input id="ha-otp-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} placeholder="you@company.com" required />
                  </div>
                  <button type="submit" className="ha-submit" disabled={loading}>
                    {loading ? "Sending code..." : "Email me a code"}
                  </button>
                  <div className="ha-alt">
                    {supportsPassword && <button type="button" className="ha-link-btn" onClick={() => setFormView("password")}>Use password instead</button>}
                    {supportsMagicLink && <button type="button" className="ha-link-btn" onClick={() => setFormView("magic-link")}>Email me a link instead</button>}
                    {supportsPhoneOtp && <button type="button" className="ha-link-btn" onClick={() => setFormView("phone-otp")}>Text me a code instead</button>}
                    <button type="button" className="ha-link-btn" onClick={() => setFormView("login")}>Use a different email</button>
                  </div>
                </form>
              )}

              {view === "email-otp-verify" && (
                <form className="ha-form" onSubmit={onVerifyEmailOtp}>
                  <div className="ha-field">
                    <label htmlFor="ha-otp-code">Code</label>
                    <input id="ha-otp-code" type="text" value={otpCode} onChange={(e) => setOtpCode(e.target.value)} inputMode="numeric" autoComplete="one-time-code" placeholder="123456" required autoFocus />
                  </div>
                  <button type="submit" className="ha-submit" disabled={loading}>
                    {loading ? "Verifying..." : "Verify code"}
                  </button>
                  <div className="ha-alt">
                    <button type="button" className="ha-link-btn" onClick={() => setFormView("email-otp")}>Send a new code</button>
                    {supportsPassword && <button type="button" className="ha-link-btn" onClick={() => setFormView("password")}>Use password instead</button>}
                    {supportsMagicLink && <button type="button" className="ha-link-btn" onClick={() => setFormView("magic-link")}>Email me a link instead</button>}
                    {supportsPhoneOtp && <button type="button" className="ha-link-btn" onClick={() => setFormView("phone-otp")}>Use phone instead</button>}
                  </div>
                </form>
              )}

              {view === "magic-link" && (
                <form className="ha-form" onSubmit={onRequestMagicLink}>
                  <div className="ha-field">
                    <label htmlFor="ha-magic-email">Email</label>
                    <input id="ha-magic-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} placeholder="you@company.com" required />
                  </div>
                  <button type="submit" className="ha-submit" disabled={loading}>
                    {loading ? "Sending link..." : "Email me a link"}
                  </button>
                  <div className="ha-alt">
                    {supportsEmailOtp && <button type="button" className="ha-link-btn" onClick={() => setFormView("email-otp")}>Use an email code instead</button>}
                    {supportsPassword && <button type="button" className="ha-link-btn" onClick={() => setFormView("password")}>Use password instead</button>}
                    {supportsPhoneOtp && <button type="button" className="ha-link-btn" onClick={() => setFormView("phone-otp")}>Text me a code instead</button>}
                    <button type="button" className="ha-link-btn" onClick={() => setFormView("login")}>Use a different email</button>
                  </div>
                </form>
              )}

              {view === "magic-link-sent" && (
                <div className="ha-form">
                  <div className="ha-success">If the account exists, a sign-in link is on the way.</div>
                  <button type="button" className="ha-submit" disabled={loading} onClick={() => setFormView("magic-link")}>
                    Request another link
                  </button>
                  <div className="ha-alt">
                    {supportsEmailOtp && <button type="button" className="ha-link-btn" onClick={() => setFormView("email-otp")}>Use an email code instead</button>}
                    {supportsPassword && <button type="button" className="ha-link-btn" onClick={() => setFormView("password")}>Use password instead</button>}
                  </div>
                </div>
              )}

              {view === "phone-otp" && (
                <form className="ha-form" onSubmit={onRequestPhoneOtp}>
                  <div className="ha-field">
                    <label htmlFor="ha-otp-phone">Phone</label>
                    <input id="ha-otp-phone" type="tel" value={phoneNumber} onChange={(e) => setPhoneNumber(e.target.value)} placeholder="+1 202 555 0105" autoComplete="tel" required />
                  </div>
                  <button type="submit" className="ha-submit" disabled={loading}>
                    {loading ? "Sending code..." : "Text me a code"}
                  </button>
                  <div className="ha-alt">
                    {supportsPassword && <button type="button" className="ha-link-btn" onClick={() => setFormView("password")}>Use password instead</button>}
                    {supportsEmailOtp && <button type="button" className="ha-link-btn" onClick={() => setFormView("email-otp")}>Use email instead</button>}
                    <button type="button" className="ha-link-btn" onClick={() => setFormView("login")}>Use a different email</button>
                  </div>
                </form>
              )}

              {view === "phone-otp-verify" && (
                <form className="ha-form" onSubmit={onVerifyPhoneOtp}>
                  <div className="ha-field">
                    <label htmlFor="ha-phone-otp-code">Code</label>
                    <input id="ha-phone-otp-code" type="text" value={otpCode} onChange={(e) => setOtpCode(e.target.value)} inputMode="numeric" autoComplete="one-time-code" placeholder="123456" required autoFocus />
                  </div>
                  <button type="submit" className="ha-submit" disabled={loading}>
                    {loading ? "Verifying..." : "Verify code"}
                  </button>
                  <div className="ha-alt">
                    <button type="button" className="ha-link-btn" onClick={() => setFormView("phone-otp")}>Send a new code</button>
                    {supportsPassword && <button type="button" className="ha-link-btn" onClick={() => setFormView("password")}>Use password instead</button>}
                    {supportsEmailOtp && <button type="button" className="ha-link-btn" onClick={() => setFormView("email-otp")}>Use email instead</button>}
                    {supportsMagicLink && <button type="button" className="ha-link-btn" onClick={() => setFormView("magic-link")}>Email me a link instead</button>}
                  </div>
                </form>
              )}

              {view === "signup" && (
                <form className="ha-form" onSubmit={onSignup}>
                  <div className="ha-row">
                    <div className="ha-field">
                      <label htmlFor="ha-fn">First name</label>
                      <input id="ha-fn" type="text" value={firstName} onChange={(e) => setFirstName(e.target.value)} placeholder="Taylor" required />
                      {fieldErrors.firstName && <p className="ha-field-error">{fieldErrors.firstName}</p>}
                    </div>
                    <div className="ha-field">
                      <label htmlFor="ha-ln">Last name</label>
                      <input id="ha-ln" type="text" value={lastName} onChange={(e) => setLastName(e.target.value)} placeholder="Morgan" required />
                      {fieldErrors.lastName && <p className="ha-field-error">{fieldErrors.lastName}</p>}
                    </div>
                  </div>
                  <div className="ha-field">
                    <label htmlFor="ha-org">Organization</label>
                    <input id="ha-org" type="text" value={organizationName} onChange={(e) => setOrganizationName(e.target.value)} placeholder="Your company name" required />
                    {fieldErrors.organizationName && <p className="ha-field-error">{fieldErrors.organizationName}</p>}
                  </div>
                  <div className="ha-field">
                    <label htmlFor="ha-su-email">Email</label>
                    <input id="ha-su-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} placeholder="taylor@company.com" required />
                    {fieldErrors.email && <p className="ha-field-error">{fieldErrors.email}</p>}
                  </div>
                  <div className="ha-field">
                    <label htmlFor="ha-su-pw">Password</label>
                    <input id="ha-su-pw" type="password" value={password} onChange={(e) => setPassword(e.target.value)} placeholder="Min. 8 characters" required />
                    {fieldErrors.password && <p className="ha-field-error">{fieldErrors.password}</p>}
                  </div>
                  <div className="ha-field">
                    <label htmlFor="ha-ref">How did you hear about us?</label>
                    <select id="ha-ref" value={referralSource} onChange={(e) => setReferralSource(e.target.value)} required>
                      <option value="">Select one</option>
                      {referralOptions.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
                    </select>
                    {fieldErrors.referralSource && <p className="ha-field-error">{fieldErrors.referralSource}</p>}
                  </div>
                  <button type="submit" className="ha-submit" disabled={loading}>
                    {loading ? "Creating account..." : "Create account"}
                  </button>
                  <div className="ha-alt">
                    Already have an account?{" "}
                    <button type="button" className="ha-link-btn" onClick={() => setFormView("login")}>Sign in</button>
                    {supportsPhoneOtp && <button type="button" className="ha-link-btn" onClick={() => setFormView("phone-otp-signup")}>Create account with SMS</button>}
                  </div>
                </form>
              )}

              {view === "phone-otp-signup" && (
                <form className="ha-form" onSubmit={onRequestPhoneOtpSignup}>
                  <div className="ha-row">
                    <div className="ha-field">
                      <label htmlFor="ha-phone-fn">First name</label>
                      <input id="ha-phone-fn" type="text" value={firstName} onChange={(e) => setFirstName(e.target.value)} placeholder="Taylor" required />
                      {fieldErrors.firstName && <p className="ha-field-error">{fieldErrors.firstName}</p>}
                    </div>
                    <div className="ha-field">
                      <label htmlFor="ha-phone-ln">Last name</label>
                      <input id="ha-phone-ln" type="text" value={lastName} onChange={(e) => setLastName(e.target.value)} placeholder="Morgan" required />
                      {fieldErrors.lastName && <p className="ha-field-error">{fieldErrors.lastName}</p>}
                    </div>
                  </div>
                  <div className="ha-field">
                    <label htmlFor="ha-phone-org">Organization</label>
                    <input id="ha-phone-org" type="text" value={organizationName} onChange={(e) => setOrganizationName(e.target.value)} placeholder="Your company name" required />
                    {fieldErrors.organizationName && <p className="ha-field-error">{fieldErrors.organizationName}</p>}
                  </div>
                  <div className="ha-field">
                    <label htmlFor="ha-su-phone">Phone</label>
                    <input id="ha-su-phone" type="tel" value={phoneNumber} onChange={(e) => setPhoneNumber(e.target.value)} placeholder="+1 202 555 0105" autoComplete="tel" required />
                  </div>
                  <div className="ha-field">
                    <label htmlFor="ha-phone-ref">How did you hear about us?</label>
                    <select id="ha-phone-ref" value={referralSource} onChange={(e) => setReferralSource(e.target.value)} required>
                      <option value="">Select one</option>
                      {referralOptions.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
                    </select>
                    {fieldErrors.referralSource && <p className="ha-field-error">{fieldErrors.referralSource}</p>}
                  </div>
                  <button type="submit" className="ha-submit" disabled={loading}>
                    {loading ? "Sending code..." : "Text me a code"}
                  </button>
                  <div className="ha-alt">
                    <button type="button" className="ha-link-btn" onClick={() => setFormView("signup")}>Use email and password</button>
                    <button type="button" className="ha-link-btn" onClick={() => setFormView("login")}>Sign in</button>
                  </div>
                </form>
              )}

              {view === "phone-otp-signup-verify" && (
                <form className="ha-form" onSubmit={onVerifyPhoneOtpSignup}>
                  <div className="ha-field">
                    <label htmlFor="ha-phone-su-code">Code</label>
                    <input id="ha-phone-su-code" type="text" value={otpCode} onChange={(e) => setOtpCode(e.target.value)} inputMode="numeric" autoComplete="one-time-code" placeholder="123456" required autoFocus />
                  </div>
                  <button type="submit" className="ha-submit" disabled={loading}>
                    {loading ? "Creating account..." : "Verify and create account"}
                  </button>
                  <div className="ha-alt">
                    <button type="button" className="ha-link-btn" onClick={() => setFormView("phone-otp-signup")}>Start over</button>
                  </div>
                </form>
              )}

              {view === "organization" && (
                <div className="ha-form">
                  <div className="ha-org-list">
                    {(viewModel?.organizationSelection ?? []).map((org) => (
                      <button key={org.id} type="button" className="ha-org-btn" disabled={loading} onClick={() => void onSelectOrganization(org.id)}>
                        <div className="ha-org-btn-icon">{org.name.charAt(0).toUpperCase()}</div>
                        <div>
                          <strong>{org.name}</strong>
                          <span>{org.role}</span>
                        </div>
                      </button>
                    ))}
                  </div>
                </div>
              )}

              {view === "consent" && (
                <div className="ha-form">
                  <div className="ha-consent-scopes">
                    <p className="ha-helper-text">This application will be able to:</p>
                    {(viewModel?.consentScopes ?? []).map((scope) => (
                      <div key={scope.scope} className="ha-consent-scope">
                        <strong>{scope.displayName}</strong>
                        {scope.description && <span>{scope.description}</span>}
                      </div>
                    ))}
                  </div>
                  <button type="button" className="ha-submit" disabled={loading} onClick={() => void onDecideConsent(true)}>
                    {loading ? "Working…" : "Allow access"}
                  </button>
                  <button type="button" className="ha-link-btn" disabled={loading} onClick={() => void onDecideConsent(false)}>
                    Deny request
                  </button>
                </div>
              )}

              {view === "mfa" && (
                <form className="ha-form" onSubmit={onVerifyMfa}>
                  <div className="ha-field">
                    <label htmlFor="ha-mfa-code">Authenticator or recovery code</label>
                    <input
                      id="ha-mfa-code"
                      type="text"
                      value={mfaCode}
                      onChange={(e) => setMfaCode(e.target.value)}
                      inputMode="numeric"
                      autoComplete="one-time-code"
                      placeholder="123456"
                      required
                      autoFocus
                    />
                  </div>
                  <button type="submit" className="ha-submit" disabled={loading || !viewModel?.mfaToken}>
                    {loading ? "Verifying..." : "Verify and continue"}
                  </button>
                  <div className="ha-alt">
                    Use a 6-digit authenticator code, or paste one of your saved recovery codes.
                  </div>
                </form>
              )}

              {view === "mfa-enroll" && (
                <div className="ha-form">
                  {viewModel?.totpEnrollment ? (
                    <form className="ha-form" onSubmit={onVerifyMfaEnrollment}>
                      <div className="ha-mfa-setup">
                        <div className="ha-mfa-qr-frame">
                          <img src={viewModel.totpEnrollment.qrCodeDataUrl} alt="Authenticator setup QR code" />
                        </div>
                        <div>
                          <strong>Scan with an authenticator app</strong>
                          <p>Use Google Authenticator, 1Password, Authy, or any TOTP-compatible app.</p>
                        </div>
                      </div>
                      <details className="ha-manual-setup">
                        <summary>Use manual setup</summary>
                        <code>{viewModel.totpEnrollment.secret}</code>
                        <code>{viewModel.totpEnrollment.provisioningUri}</code>
                      </details>
                      <div className="ha-field">
                        <label htmlFor="ha-mfa-enroll-code">Verification code</label>
                        <input
                          id="ha-mfa-enroll-code"
                          type="text"
                          value={mfaEnrollmentCode}
                          onChange={(e) => setMfaEnrollmentCode(e.target.value)}
                          inputMode="numeric"
                          autoComplete="one-time-code"
                          placeholder="123456"
                          required
                          autoFocus
                        />
                      </div>
                      <button type="submit" className="ha-submit" disabled={loading || mfaEnrollmentCode.trim().length < 6}>
                        {loading ? "Verifying..." : "Verify and continue"}
                      </button>
                    </form>
                  ) : (
                    <>
                      <p className="ha-helper-text">This organization requires an authenticator app before you can continue.</p>
                      <button type="button" className="ha-submit" onClick={() => void onStartMfaEnrollment()} disabled={loading || !viewModel?.mfaToken}>
                        {loading ? "Starting..." : "Add authenticator app"}
                      </button>
                    </>
                  )}
                </div>
              )}

              {showProviderButtons && (
                <div className="ha-providers">
                  <div className="ha-divider"><span>or</span></div>
                  {(viewModel?.providers ?? []).map((provider) => (
                    <button key={provider.connectionId} type="button" className="ha-provider-btn" disabled={loading} onClick={() => void onProviderStart(provider.connectionId)}>
                      <ProviderBadge provider={provider} />
                      <span className="ha-provider-btn-label">Continue with {provider.displayName}</span>
                    </button>
                  ))}
                </div>
              )}
            </>
          )}

          <div className="ha-footer">
            <Link href="/">← Back to Northwind Retail</Link>
          </div>
        </div>
      </div>
    </div>
  );
}

function ProviderBadge({ provider }: { provider: HeadlessProvider }) {
  if (provider.logoDataUrl) {
    return (
      <span className="ha-provider-logo-badge" aria-hidden="true">
        <img src={provider.logoDataUrl} alt="" />
      </span>
    );
  }

  return (
    <span className="ha-provider-logo-badge ha-provider-logo-badge--fallback" aria-hidden="true">
      {getProviderMonogram(provider.displayName)}
    </span>
  );
}

function HeadlessFlowStarter({ initialView, nextPath }: { initialView: "login" | "signup"; nextPath: string }) {
  const [starting, setStarting] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [selectedView, setSelectedView] = useState<"login" | "signup">(initialView);

  const startFlow = async (flowView: "login" | "signup") => {
    setSelectedView(flowView);
    setStarting(true);
    setErr(null);
    try {
      await startHostedSqlOSSignIn(flowView, nextPath);
    } catch (error) {
      setErr(error instanceof Error ? error.message : "Failed to start.");
      setStarting(false);
    }
  };

  return (
    <div className="ha-form">
      <p className="muted" style={{ fontSize: 13, lineHeight: 1.6, marginBottom: 8 }}>
        This page demonstrates <strong>headless auth</strong> — your app owns the UI while SqlOS handles the OAuth protocol underneath.
      </p>
      {err && <div className="ha-error">{err}</div>}
      <button className="ha-submit" onClick={() => void startFlow(initialView)} disabled={starting}>
        {starting && selectedView === initialView ? "Redirecting..." : initialView === "signup" ? "Start signup flow" : "Start sign in flow"}
      </button>
      <button className="ha-provider-btn" onClick={() => void startFlow(initialView === "signup" ? "login" : "signup")} disabled={starting}>
        {initialView === "signup" ? "Or sign in instead" : "Or create an account"}
      </button>
    </div>
  );
}
