"use client";

import { useEffect, useState, useCallback } from "react";
import { useSearchParams } from "next/navigation";
import { signIn } from "next-auth/react";
import { jwtDecode } from "jwt-decode";
import Link from "next/link";
import {
  getHeadlessRequest,
  headlessIdentify,
  headlessPasswordLogin,
  headlessRequestPasswordResetEmail,
  headlessRequestEmailOtp,
  headlessRequestPhoneOtp,
  headlessRequestPhoneOtpSignup,
  headlessResetPassword,
  headlessSelectOrganization,
  headlessSignup,
  headlessStartProvider,
  headlessStartMfaTotpEnrollment,
  headlessVerifyEmailOtp,
  headlessVerifyMfa,
  headlessVerifyMfaTotpEnrollment,
  headlessVerifyPhoneOtp,
  headlessVerifyPhoneOtpSignup,
  type HeadlessViewModel,
  type HeadlessActionResult,
  type HeadlessProvider,
} from "@/lib/sqlos-headless";
import {
  getExampleAuthServerUrl,
  getExampleClientId,
  getExampleRedirectUri,
  createOpaqueToken,
  createCodeChallenge,
  persistSqlOSAuthFlow,
  readSqlOSAuthFlow,
  clearSqlOSAuthFlow,
} from "@/lib/sqlos-auth";

type DecodedToken = {
  exp: number;
  sub?: string;
  email?: string;
  name?: string;
  org_id?: string;
  sid?: string;
};

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
  const initialError = searchParams.get("error");
  const initialEmail = searchParams.get("email") || "";
  const pendingToken = searchParams.get("pendingToken");
  const initialDisplayName = searchParams.get("displayName") || "";
  const initialResetToken = searchParams.get("token") || "";
  const nextPath = searchParams.get("next") || "/retail";

  const [view, setView] = useState(initialView);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(initialError);
  const [notice, setNotice] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [viewModel, setViewModel] = useState<HeadlessViewModel | null>(null);

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

  useEffect(() => {
    if (!requestId) return;

    const load = async () => {
      try {
        const vm = await getHeadlessRequest(
          requestId,
          initialView,
          initialError,
          pendingToken,
          initialEmail,
          initialDisplayName,
        );
        setViewModel(vm);
        if (vm.view) setView(vm.view);
        if (vm.error) setError(vm.error);
        if (vm.email) setEmail(vm.email);
        if (vm.email) setResetEmail(vm.email);
        if (vm.phoneNumber) setPhoneNumber(vm.phoneNumber);
        if (vm.displayName && !firstName && !lastName && initialDisplayName) {
          const [first = "", ...rest] = vm.displayName.split(" ");
          setFirstName(first);
          setLastName(rest.join(" "));
        }
        if (vm.fieldErrors) setFieldErrors(vm.fieldErrors);
      } catch (err) {
        setError(err instanceof Error ? err.message : "Failed to load authorization request.");
      }
    };

    void load();
  }, [requestId, initialView, initialError, pendingToken, initialEmail, initialDisplayName]);

  useEffect(() => {
    if (initialResetToken) {
      setResetToken(initialResetToken);
      setView("password-reset");
    }
  }, [initialResetToken]);

  const handleResult = useCallback(async (result: HeadlessActionResult) => {
    setNotice(null);
    if (result.type === "redirect" && result.redirectUrl) {
      const url = new URL(result.redirectUrl);
      const code = url.searchParams.get("code");

      // If the redirect is to a custom scheme (e.g. mobile app), don't exchange
      // the code here — let the native app handle the token exchange itself.
      if (code && !url.protocol.startsWith("http")) {
        window.location.href = result.redirectUrl;
        return;
      }

      if (code) {
        const flow = readSqlOSAuthFlow();
        const tokenRes = await fetch(`${getExampleAuthServerUrl()}/token`, {
          method: "POST",
          headers: { "Content-Type": "application/x-www-form-urlencoded" },
          body: new URLSearchParams({
            grant_type: "authorization_code",
            code,
            client_id: getExampleClientId(),
            redirect_uri: getExampleRedirectUri(),
            code_verifier: flow.verifier || "",
          }),
        });

        const tokenData = await tokenRes.json();
        if (!tokenRes.ok || !tokenData.access_token) {
          setError(tokenData.error_description || tokenData.error || "Token exchange failed.");
          return;
        }

        const decoded = jwtDecode<DecodedToken>(tokenData.access_token);
        const signInResult = await signIn("credentials", {
          redirect: false,
          accessToken: tokenData.access_token,
          refreshToken: tokenData.refresh_token,
          userId: decoded.sub ?? "",
          email: decoded.email ?? "",
          displayName: decoded.name ?? decoded.email ?? "User",
          organizationId: decoded.org_id ?? null,
          sessionId: decoded.sid ?? "",
        });

        if (!signInResult || signInResult.error) {
          setError(signInResult?.error || "Session creation failed.");
          return;
        }

        clearSqlOSAuthFlow();
        window.location.replace(flow.nextPath || "/retail");
        return;
      }

      window.location.href = result.redirectUrl;
      return;
    }

    if (result.viewModel) {
      const nextViewModel =
        result.viewModel.view === "mfa-enroll" && !result.viewModel.totpEnrollment && viewModel?.totpEnrollment
          ? { ...result.viewModel, totpEnrollment: viewModel.totpEnrollment }
          : result.viewModel;
      setViewModel(nextViewModel);
      if (nextViewModel.view) setView(nextViewModel.view);
      if (nextViewModel.error) setError(nextViewModel.error);
      if (nextViewModel.email) {
        setEmail(nextViewModel.email);
        setResetEmail(nextViewModel.email);
      }
      if (nextViewModel.phoneNumber) setPhoneNumber(nextViewModel.phoneNumber);
      if (nextViewModel.challengeToken) setOtpCode("");
      if (nextViewModel.mfaToken) setMfaCode("");
      if (nextViewModel.totpEnrollment) setMfaEnrollmentCode("");
      setFieldErrors(nextViewModel.fieldErrors ?? {});
    }
  }, [viewModel?.totpEnrollment]);

  const onStartMfaEnrollment = useCallback(async () => {
    if (!requestId || !viewModel?.mfaToken) return;
    setLoading(true); setError(null); setFieldErrors({});
    try {
      await handleResult(await headlessStartMfaTotpEnrollment(requestId, viewModel.mfaToken, "Authenticator app"));
    } catch (err) {
      setError(err instanceof Error ? err.message : "We could not start authenticator enrollment.");
    } finally {
      setLoading(false);
    }
  }, [handleResult, requestId, viewModel?.mfaToken]);

  useEffect(() => {
    if (view !== "mfa-enroll" || !requestId || !viewModel?.mfaToken || viewModel.totpEnrollment) return;
    if (startedMfaEnrollmentToken === viewModel.mfaToken) return;
    setStartedMfaEnrollmentToken(viewModel.mfaToken);
    void onStartMfaEnrollment();
  }, [onStartMfaEnrollment, requestId, startedMfaEnrollmentToken, view, viewModel?.mfaToken, viewModel?.totpEnrollment]);

  const onIdentify = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!requestId) return;
    setLoading(true); setError(null); setFieldErrors({});
    try { await handleResult(await headlessIdentify(requestId, email)); }
    catch (err) { setError(err instanceof Error ? err.message : "We could not start sign in."); }
    finally { setLoading(false); }
  };

  const onLogin = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!requestId) return;
    setLoading(true); setError(null); setFieldErrors({});
    try { await handleResult(await headlessPasswordLogin(requestId, email, password)); }
    catch (err) { setError(err instanceof Error ? err.message : "Login failed."); }
    finally { setLoading(false); }
  };

  const onRequestPasswordReset = async (event: React.FormEvent) => {
    event.preventDefault();
    setLoading(true); setError(null); setNotice(null); setFieldErrors({});
    try {
      const targetEmail = (resetEmail || email).trim();
      const result = await headlessRequestPasswordResetEmail(targetEmail, requestId);
      setNotice(result.message || "If the account can be reset, a reset email is on the way.");
      setView("forgot-password-sent");
    } catch (err) {
      setError(err instanceof Error ? err.message : "We could not request password recovery.");
    } finally {
      setLoading(false);
    }
  };

  const onResetPassword = async (event: React.FormEvent) => {
    event.preventDefault();
    setLoading(true); setError(null); setNotice(null); setFieldErrors({});
    try {
      if (!resetToken.trim()) {
        throw new Error("Password reset token is missing.");
      }
      if (newPassword !== confirmNewPassword) {
        setFieldErrors({ confirmNewPassword: "Passwords do not match." });
        return;
      }
      await headlessResetPassword(resetToken, newPassword);
      setPassword("");
      setNewPassword("");
      setConfirmNewPassword("");
      setNotice("Your password has been reset. Sign in with your new password.");
      setView("login");
    } catch (err) {
      setError(err instanceof Error ? err.message : "We could not reset your password.");
    } finally {
      setLoading(false);
    }
  };

  const onRequestEmailOtp = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!requestId) return;
    setLoading(true); setError(null); setFieldErrors({});
    try { await handleResult(await headlessRequestEmailOtp(requestId, email)); }
    catch (err) { setError(err instanceof Error ? err.message : "We could not send a sign-in code."); }
    finally { setLoading(false); }
  };

  const onVerifyEmailOtp = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!requestId) return;
    const challengeToken = viewModel?.challengeToken;
    if (!challengeToken) {
      setError("Request a new sign-in code first.");
      return;
    }

    setLoading(true); setError(null); setFieldErrors({});
    try { await handleResult(await headlessVerifyEmailOtp(requestId, challengeToken, otpCode)); }
    catch (err) { setError(err instanceof Error ? err.message : "The sign-in code was rejected."); }
    finally { setLoading(false); }
  };

  const onRequestPhoneOtp = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!requestId) return;
    setLoading(true); setError(null); setFieldErrors({});
    try { await handleResult(await headlessRequestPhoneOtp(requestId, phoneNumber)); }
    catch (err) { setError(err instanceof Error ? err.message : "We could not send a sign-in code."); }
    finally { setLoading(false); }
  };

  const onVerifyPhoneOtp = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!requestId) return;
    const challengeToken = viewModel?.challengeToken;
    if (!challengeToken) {
      setError("Request a new sign-in code first.");
      return;
    }

    setLoading(true); setError(null); setFieldErrors({});
    try { await handleResult(await headlessVerifyPhoneOtp(requestId, challengeToken, otpCode)); }
    catch (err) { setError(err instanceof Error ? err.message : "The sign-in code was rejected."); }
    finally { setLoading(false); }
  };

  const onSignup = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!requestId) return;
    setLoading(true); setError(null); setFieldErrors({});
    try {
      await handleResult(await headlessSignup(requestId, buildDisplayName(firstName, lastName, email), email, password, organizationName, { referralSource, firstName, lastName }));
    } catch (err) { setError(err instanceof Error ? err.message : "Signup failed."); }
    finally { setLoading(false); }
  };

  const onRequestPhoneOtpSignup = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!requestId) return;
    setLoading(true); setError(null); setFieldErrors({});
    try {
      await handleResult(await headlessRequestPhoneOtpSignup(
        requestId,
        buildDisplayName(firstName, lastName, phoneNumber),
        phoneNumber,
        organizationName,
        { referralSource, firstName, lastName },
      ));
    } catch (err) { setError(err instanceof Error ? err.message : "Signup failed."); }
    finally { setLoading(false); }
  };

  const onVerifyPhoneOtpSignup = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!requestId) return;
    const challengeToken = viewModel?.challengeToken;
    const signupToken = viewModel?.signupToken;
    if (!challengeToken || !signupToken) {
      setError("Request a new sign-up code first.");
      return;
    }

    setLoading(true); setError(null); setFieldErrors({});
    try { await handleResult(await headlessVerifyPhoneOtpSignup(requestId, signupToken, challengeToken, otpCode)); }
    catch (err) { setError(err instanceof Error ? err.message : "The sign-up code was rejected."); }
    finally { setLoading(false); }
  };

  const onProviderStart = async (connectionId: string) => {
    if (!requestId) return;
    setLoading(true); setError(null);
    try { await handleResult(await headlessStartProvider(requestId, connectionId, email || undefined)); }
    catch (err) { setError(err instanceof Error ? err.message : "Provider auth failed."); }
    finally { setLoading(false); }
  };

  const onSelectOrganization = async (organizationId: string) => {
    const activePendingToken = viewModel?.pendingToken ?? pendingToken;
    if (!activePendingToken) return;
    setLoading(true); setError(null);
    try { await handleResult(await headlessSelectOrganization(activePendingToken, organizationId)); }
    catch (err) { setError(err instanceof Error ? err.message : "Organization selection failed."); }
    finally { setLoading(false); }
  };

  const onVerifyMfa = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!requestId || !viewModel?.mfaToken) return;
    setLoading(true); setError(null); setFieldErrors({});
    try {
      await handleResult(await headlessVerifyMfa(requestId, viewModel.mfaToken, mfaCode));
    } catch (err) {
      setError(err instanceof Error ? err.message : "The second-factor code was rejected.");
    } finally {
      setLoading(false);
    }
  };

  const onVerifyMfaEnrollment = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!requestId || !viewModel?.mfaToken || !viewModel.totpEnrollment) return;
    setLoading(true); setError(null); setFieldErrors({});
    try {
      await handleResult(await headlessVerifyMfaTotpEnrollment(
        requestId,
        viewModel.mfaToken,
        viewModel.totpEnrollment.enrollmentToken,
        mfaEnrollmentCode,
      ));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Authenticator enrollment failed.");
    } finally {
      setLoading(false);
    }
  };

  const isSignup = view === "signup" || view === "phone-otp-signup" || view === "phone-otp-signup-verify";
  const isMfa = view === "mfa" || view === "mfa-enroll";
  const isRecovery = view === "forgot-password" || view === "forgot-password-sent" || view === "password-reset";
  const showProviderButtons = (view === "login" || view === "identify" || view === "signup") && (viewModel?.providers?.length ?? 0) > 0;
  const supportsPassword = !!viewModel?.settings?.localPasswordRuntimeEnabled
    && (viewModel?.settings?.enabledCredentialTypes ?? []).includes("password");
  const supportsEmailOtp = !!viewModel?.settings?.emailOtpRuntimeConfigured
    && (viewModel?.settings?.enabledCredentialTypes ?? []).includes("email_otp");
  const supportsPhoneOtp = !!viewModel?.settings?.phoneOtpRuntimeConfigured
    && (viewModel?.settings?.enabledCredentialTypes ?? []).includes("phone_otp");

  const headline = isSignup
    ? "Start your free trial"
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
                {fieldErrors.confirmNewPassword && <p className="ha-field-error">{fieldErrors.confirmNewPassword}</p>}
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
              {(view === "login" || view === "identify") && (
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
                    <button type="button" className="ha-link-btn" onClick={() => setView("signup")}>Sign up</button>
                    {supportsPhoneOtp && <button type="button" className="ha-link-btn" onClick={() => setView("phone-otp")}>Use phone instead</button>}
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
                    <button type="button" className="ha-link-btn" onClick={() => setView("login")}>Use a different email</button>
                    <button type="button" className="ha-link-btn" onClick={() => { setResetEmail(email); setView("forgot-password"); }}>Forgot password?</button>
                    {supportsEmailOtp && <button type="button" className="ha-link-btn" onClick={() => setView("email-otp")}>Email me a code instead</button>}
                    {supportsPhoneOtp && <button type="button" className="ha-link-btn" onClick={() => setView("phone-otp")}>Text me a code instead</button>}
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
                    <button type="button" className="ha-link-btn" onClick={() => setView(email ? "password" : "login")}>Back to sign in</button>
                  </div>
                </form>
              )}

              {view === "forgot-password-sent" && (
                <div className="ha-form">
                  <button type="button" className="ha-submit" onClick={() => setView("login")}>
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
                    {fieldErrors.confirmNewPassword && <p className="ha-field-error">{fieldErrors.confirmNewPassword}</p>}
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
                    {supportsPassword && <button type="button" className="ha-link-btn" onClick={() => setView("password")}>Use password instead</button>}
                    {supportsPhoneOtp && <button type="button" className="ha-link-btn" onClick={() => setView("phone-otp")}>Text me a code instead</button>}
                    <button type="button" className="ha-link-btn" onClick={() => setView("login")}>Use a different email</button>
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
                    <button type="button" className="ha-link-btn" onClick={() => setView("email-otp")}>Send a new code</button>
                    {supportsPassword && <button type="button" className="ha-link-btn" onClick={() => setView("password")}>Use password instead</button>}
                    {supportsPhoneOtp && <button type="button" className="ha-link-btn" onClick={() => setView("phone-otp")}>Use phone instead</button>}
                  </div>
                </form>
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
                    {supportsPassword && <button type="button" className="ha-link-btn" onClick={() => setView("password")}>Use password instead</button>}
                    {supportsEmailOtp && <button type="button" className="ha-link-btn" onClick={() => setView("email-otp")}>Use email instead</button>}
                    <button type="button" className="ha-link-btn" onClick={() => setView("login")}>Use a different email</button>
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
                    <button type="button" className="ha-link-btn" onClick={() => setView("phone-otp")}>Send a new code</button>
                    {supportsPassword && <button type="button" className="ha-link-btn" onClick={() => setView("password")}>Use password instead</button>}
                    {supportsEmailOtp && <button type="button" className="ha-link-btn" onClick={() => setView("email-otp")}>Use email instead</button>}
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
                    <button type="button" className="ha-link-btn" onClick={() => setView("login")}>Sign in</button>
                    {supportsPhoneOtp && <button type="button" className="ha-link-btn" onClick={() => setView("phone-otp-signup")}>Create account with SMS</button>}
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
                    <button type="button" className="ha-link-btn" onClick={() => setView("signup")}>Use email and password</button>
                    <button type="button" className="ha-link-btn" onClick={() => setView("login")}>Sign in</button>
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
                    <button type="button" className="ha-link-btn" onClick={() => setView("phone-otp-signup")}>Start over</button>
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
      const verifier = createOpaqueToken(48);
      const state = createOpaqueToken(24);
      const challenge = await createCodeChallenge(verifier);
      persistSqlOSAuthFlow(flowView, state, verifier, nextPath);
      const url = new URL(`${getExampleAuthServerUrl()}/authorize`);
      url.searchParams.set("response_type", "code");
      url.searchParams.set("client_id", getExampleClientId());
      url.searchParams.set("redirect_uri", getExampleRedirectUri());
      url.searchParams.set("state", state);
      url.searchParams.set("code_challenge", challenge);
      url.searchParams.set("code_challenge_method", "S256");
      if (flowView === "signup") url.searchParams.set("view", "signup");
      window.location.replace(url.toString());
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
