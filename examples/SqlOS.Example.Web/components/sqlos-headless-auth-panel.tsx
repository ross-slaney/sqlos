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
  headlessSelectOrganization,
  headlessSignup,
  headlessStartProvider,
  type HeadlessViewModel,
  type HeadlessActionResult,
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
  { value: "emcy", label: "Emcy or MCP integration work" },
  { value: "friend", label: "Recommendation from a teammate" },
  { value: "review", label: "Build vs. buy auth evaluation" },
];

function buildDisplayName(firstName: string, lastName: string, fallbackEmail: string) {
  const combined = `${firstName} ${lastName}`.trim();
  return combined || fallbackEmail.trim() || "Example User";
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
  const nextPath = searchParams.get("next") || "/retail";

  const [view, setView] = useState(initialView);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(initialError);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [viewModel, setViewModel] = useState<HeadlessViewModel | null>(null);

  const [email, setEmail] = useState(initialEmail);
  const [password, setPassword] = useState("");
  const [organizationName, setOrganizationName] = useState("");
  const [firstName, setFirstName] = useState("");
  const [lastName, setLastName] = useState("");
  const [referralSource, setReferralSource] = useState("");

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

  const handleResult = useCallback(async (result: HeadlessActionResult) => {
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
      setViewModel(result.viewModel);
      if (result.viewModel.view) setView(result.viewModel.view);
      if (result.viewModel.error) setError(result.viewModel.error);
      if (result.viewModel.email) setEmail(result.viewModel.email);
      setFieldErrors(result.viewModel.fieldErrors ?? {});
    }
  }, []);

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

  const onSignup = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!requestId) return;
    setLoading(true); setError(null); setFieldErrors({});
    try {
      await handleResult(await headlessSignup(requestId, buildDisplayName(firstName, lastName, email), email, password, organizationName, { referralSource, firstName, lastName }));
    } catch (err) { setError(err instanceof Error ? err.message : "Signup failed."); }
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

  const isSignup = view === "signup";
  const showProviderButtons = (view === "login" || view === "identify" || view === "signup") && (viewModel?.providers?.length ?? 0) > 0;

  const headline = isSignup ? "Start your free trial" : view === "organization" ? "Choose workspace" : "Welcome back";
  const subtitle = isSignup
    ? "Create your account and start managing retail operations in minutes."
    : view === "organization"
      ? "Select the organization you'd like to sign in to."
      : "Sign in to your Northwind Retail account.";
  const testimonialQuote = isSignup
    ? "Setting up took less than five minutes. We had our entire team onboarded before lunch."
    : "I love that I can see exactly my stores. No noise, no clutter — just the data I need.";
  const testimonialName = isSignup ? "Marcus Rivera" : "Priya Sharma";
  const testimonialRole = isSignup ? "Head of Retail Ops, FreshMart" : "Store Manager, Target #100";

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

          {!requestId ? (
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

              {showProviderButtons && (
                <div className="ha-providers">
                  <div className="ha-divider"><span>or</span></div>
                  {(viewModel?.providers ?? []).map((provider) => (
                    <button key={provider.connectionId} type="button" className="ha-provider-btn" disabled={loading} onClick={() => void onProviderStart(provider.connectionId)}>
                      Continue with {provider.displayName}
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
