"use client";

import { useEffect, useState, useCallback } from "react";
import { useSearchParams } from "next/navigation";
import { signIn } from "next-auth/react";
import { jwtDecode } from "jwt-decode";
import {
  getHeadlessRequest,
  headlessIdentify,
  headlessPasswordLogin,
  headlessSelectOrganization,
  headlessSignup,
  headlessStartProvider,
  type HeadlessViewModel,
  type HeadlessActionResult,
  type HeadlessSettings,
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

const productProofPoints = [
  "App-native login and signup layouts",
  "Real PKCE authorization flow under the hood",
  "Custom signup field persisted by your app",
];

const launchChecklist = [
  "SqlOS owns /authorize, /token, sessions, and code issuance",
  "Your app owns the HTML, experiments, and conversion copy",
  "The popup can look like your product instead of hosted auth chrome",
];

function buildDisplayName(firstName: string, lastName: string, fallbackEmail: string) {
  const combined = `${firstName} ${lastName}`.trim();
  return combined || fallbackEmail.trim() || "Example User";
}

export function SqlOSHeadlessAuthPanel() {
  const searchParams = useSearchParams();
  const requestId = searchParams.get("request");
  const initialView = searchParams.get("view") || "login";
  const initialError = searchParams.get("error");
  const initialEmail = searchParams.get("email") || "";
  const pendingToken = searchParams.get("pendingToken");
  const initialDisplayName = searchParams.get("displayName") || "";

  const [view, setView] = useState(initialView);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(initialError);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [settings, setSettings] = useState<HeadlessSettings | null>(null);
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
        setSettings(vm.settings ?? null);
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
        window.location.replace(flow.nextPath || "/app");
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
    setLoading(true);
    setError(null);
    setFieldErrors({});
    try {
      const result = await headlessIdentify(requestId, email);
      await handleResult(result);
    } catch (err) {
      setError(err instanceof Error ? err.message : "We could not start sign in.");
    } finally {
      setLoading(false);
    }
  };

  const onLogin = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!requestId) return;
    setLoading(true);
    setError(null);
    setFieldErrors({});
    try {
      const result = await headlessPasswordLogin(requestId, email, password);
      await handleResult(result);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Login failed.");
    } finally {
      setLoading(false);
    }
  };

  const onSignup = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!requestId) return;
    setLoading(true);
    setError(null);
    setFieldErrors({});
    try {
      const result = await headlessSignup(
        requestId,
        buildDisplayName(firstName, lastName, email),
        email,
        password,
        organizationName,
        {
          referralSource,
          firstName,
          lastName,
        },
      );
      await handleResult(result);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Signup failed.");
    } finally {
      setLoading(false);
    }
  };

  const onProviderStart = async (connectionId: string) => {
    if (!requestId) return;
    setLoading(true);
    setError(null);
    try {
      const result = await headlessStartProvider(requestId, connectionId, email || undefined);
      await handleResult(result);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Provider auth failed.");
    } finally {
      setLoading(false);
    }
  };

  const onSelectOrganization = async (organizationId: string) => {
    const activePendingToken = viewModel?.pendingToken ?? pendingToken;
    if (!activePendingToken) return;
    setLoading(true);
    setError(null);
    try {
      const result = await headlessSelectOrganization(activePendingToken, organizationId);
      await handleResult(result);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Organization selection failed.");
    } finally {
      setLoading(false);
    }
  };

  const primaryColor = settings?.primaryColor || "#55644c";
  const accentColor = settings?.accentColor || "#1f271b";
  const backgroundColor = settings?.backgroundColor || "#f7f7f1";
  const showProviderButtons = (view === "login" || view === "identify" || view === "signup") && (viewModel?.providers?.length ?? 0) > 0;
  const title = settings?.pageTitle || "Own the authorize experience";
  const subtitle = settings?.pageSubtitle || "SqlOS keeps the authorization server protocol intact while this app owns the entire UI.";

  return (
    <div
      className="headless-auth-shell"
      style={{
        ["--headless-primary" as never]: primaryColor,
        ["--headless-accent" as never]: accentColor,
        ["--headless-surface" as never]: backgroundColor,
      }}
    >
      <section className="headless-auth-showcase">
        <div className="headless-auth-badge">Headless Auth Server Demo</div>
        <h1>{title}</h1>
        <p>{subtitle}</p>
        <div className="headless-auth-highlight">
          <strong>What this example proves</strong>
          <ul>
            {productProofPoints.map((item) => (
              <li key={item}>{item}</li>
            ))}
          </ul>
        </div>
        <div className="headless-auth-proof-grid">
          <article>
            <span>App-owned</span>
            <strong>Layouts, copy, and experiments</strong>
            <p>The authorize popup can share the same design system as your marketing and product funnel.</p>
          </article>
          <article>
            <span>SqlOS-owned</span>
            <strong>Authorize, token, PKCE, refresh</strong>
            <p>No bridge flow, no app-issued auth codes, and no protocol drift when the UI changes.</p>
          </article>
        </div>
        <div className="headless-auth-mock">
          <div className="headless-auth-mock-window">
            <div className="headless-auth-mock-toolbar">
              <span />
              <span />
              <span />
            </div>
            <div className="headless-auth-mock-content">
              <div className="headless-auth-metric">
                <label>Custom field</label>
                <strong>Referral source</strong>
              </div>
              <div className="headless-auth-metric">
                <label>Auth boundary</label>
                <strong>Still real OAuth</strong>
              </div>
              <div className="headless-auth-metric wide">
                <label>Ship it</label>
                <strong>Run the example, sign up through headless mode, then inspect the saved profile on the app page.</strong>
              </div>
            </div>
          </div>
        </div>
      </section>

      <section className="headless-auth-form-surface">
        <div className="headless-auth-form-header">
          <div>
            <p className="headless-auth-eyebrow">App-native authorize UI</p>
            <h2>{view === "signup" ? "Create an account" : view === "organization" ? "Select an organization" : "Sign in"}</h2>
            <p>
              {view === "signup"
                ? "This signup form captures app-owned data without moving OAuth out of SqlOS."
                : view === "organization"
                  ? "Choose the organization that should receive the authorization code."
                  : "Start with email. SqlOS will still handle home realm discovery, sessions, and token issuance."}
            </p>
          </div>
          {!requestId ? (
            <div className="headless-auth-pill">No request yet</div>
          ) : (
            <div className="headless-auth-pill">Request {requestId.slice(-6)}</div>
          )}
        </div>

        <div className="headless-auth-steps">
          <span className={`headless-auth-step${view === "login" || view === "identify" || view === "password" ? " active" : ""}`}>1. Identify</span>
          <span className={`headless-auth-step${view === "signup" ? " active" : ""}`}>2. Signup</span>
          <span className={`headless-auth-step${view === "organization" ? " active" : ""}`}>3. Organization</span>
        </div>

        {error ? <p className="headless-auth-error-banner">{error}</p> : null}

        {!requestId ? (
          <HeadlessFlowStarter initialView={view === "signup" ? "signup" : "login"} />
        ) : (
          <>
            {(view === "login" || view === "identify") && (
              <form className="headless-auth-form" onSubmit={onIdentify}>
                <label htmlFor="headless-id-email">Work email</label>
                <input
                  id="headless-id-email"
                  type="email"
                  value={email}
                  onChange={(event) => setEmail(event.target.value)}
                  placeholder="name@company.com"
                  required
                />
                {fieldErrors.email ? <p className="headless-auth-field-error">{fieldErrors.email}</p> : null}
                <button type="submit" disabled={loading} style={{ background: primaryColor }}>
                  {loading ? "Checking your workspace..." : "Continue"}
                </button>
                <button type="button" className="secondary" onClick={() => setView("signup")}>
                  Need an account? Start signup
                </button>
              </form>
            )}

            {view === "password" && (
              <form className="headless-auth-form" onSubmit={onLogin}>
                <label htmlFor="headless-password-email">Email</label>
                <input
                  id="headless-password-email"
                  type="email"
                  value={email}
                  onChange={(event) => setEmail(event.target.value)}
                  required
                />
                <label htmlFor="headless-password">Password</label>
                <input
                  id="headless-password"
                  type="password"
                  value={password}
                  onChange={(event) => setPassword(event.target.value)}
                  placeholder="Enter your password"
                  required
                />
                {fieldErrors.password ? <p className="headless-auth-field-error">{fieldErrors.password}</p> : null}
                <button type="submit" disabled={loading} style={{ background: primaryColor }}>
                  {loading ? "Signing in..." : "Complete sign in"}
                </button>
                <button type="button" className="secondary" onClick={() => setView("login")}>
                  Use a different email
                </button>
              </form>
            )}

            {view === "signup" && (
              <form className="headless-auth-form" onSubmit={onSignup}>
                <div className="headless-auth-grid">
                  <div>
                    <label htmlFor="headless-first-name">First name</label>
                    <input
                      id="headless-first-name"
                      type="text"
                      value={firstName}
                      onChange={(event) => setFirstName(event.target.value)}
                      placeholder="Taylor"
                      required
                    />
                    {fieldErrors.firstName ? <p className="headless-auth-field-error">{fieldErrors.firstName}</p> : null}
                  </div>
                  <div>
                    <label htmlFor="headless-last-name">Last name</label>
                    <input
                      id="headless-last-name"
                      type="text"
                      value={lastName}
                      onChange={(event) => setLastName(event.target.value)}
                      placeholder="Morgan"
                      required
                    />
                    {fieldErrors.lastName ? <p className="headless-auth-field-error">{fieldErrors.lastName}</p> : null}
                  </div>
                </div>

                <label htmlFor="headless-signup-org">Organization name</label>
                <input
                  id="headless-signup-org"
                  type="text"
                  value={organizationName}
                  onChange={(event) => setOrganizationName(event.target.value)}
                  placeholder="Northwind Retail"
                  required
                />
                {fieldErrors.organizationName ? <p className="headless-auth-field-error">{fieldErrors.organizationName}</p> : null}

                <label htmlFor="headless-signup-email">Work email</label>
                <input
                  id="headless-signup-email"
                  type="email"
                  value={email}
                  onChange={(event) => setEmail(event.target.value)}
                  placeholder="taylor@northwind.dev"
                  required
                />
                {fieldErrors.email ? <p className="headless-auth-field-error">{fieldErrors.email}</p> : null}

                <label htmlFor="headless-signup-password">Password</label>
                <input
                  id="headless-signup-password"
                  type="password"
                  value={password}
                  onChange={(event) => setPassword(event.target.value)}
                  placeholder="Create a password"
                  required
                />
                {fieldErrors.password ? <p className="headless-auth-field-error">{fieldErrors.password}</p> : null}

                <label htmlFor="headless-referral-source">How did you hear about SqlOS?</label>
                <select
                  id="headless-referral-source"
                  value={referralSource}
                  onChange={(event) => setReferralSource(event.target.value)}
                  required
                >
                  <option value="">Select one</option>
                  {referralOptions.map((option) => (
                    <option key={option.value} value={option.value}>
                      {option.label}
                    </option>
                  ))}
                </select>
                {fieldErrors.referralSource ? <p className="headless-auth-field-error">{fieldErrors.referralSource}</p> : null}

                <button type="submit" disabled={loading} style={{ background: primaryColor }}>
                  {loading ? "Creating your account..." : "Create account"}
                </button>
                <button type="button" className="secondary" onClick={() => setView("login")}>
                  Already have an account?
                </button>
              </form>
            )}

            {view === "organization" && (
              <div className="headless-auth-selection">
                <p className="muted">This user belongs to multiple organizations. Choose the workspace that should receive the authorization code.</p>
                <div className="headless-auth-selection-list">
                  {(viewModel?.organizationSelection ?? []).map((organization) => (
                    <button
                      key={organization.id}
                      type="button"
                      className="headless-auth-org-button"
                      disabled={loading}
                      onClick={() => void onSelectOrganization(organization.id)}
                    >
                      <strong>{organization.name}</strong>
                      <span>{organization.role}</span>
                    </button>
                  ))}
                </div>
              </div>
            )}

            {showProviderButtons && (
              <div className="headless-auth-provider-block">
                <div className="separator">or continue with</div>
                <div className="stacked-actions">
                  {(viewModel?.providers ?? []).map((provider) => (
                    <button
                      key={provider.connectionId}
                      type="button"
                      className="secondary"
                      disabled={loading}
                      onClick={() => void onProviderStart(provider.connectionId)}
                    >
                      Continue with {provider.displayName}
                    </button>
                  ))}
                </div>
              </div>
            )}
          </>
        )}

        <aside className="headless-auth-sidebar-note">
          <h3>Why this screen matters</h3>
          <ul>
            {launchChecklist.map((item) => (
              <li key={item}>{item}</li>
            ))}
          </ul>
        </aside>
      </section>
    </div>
  );
}

function HeadlessFlowStarter({ initialView }: { initialView: "login" | "signup" }) {
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

      persistSqlOSAuthFlow(flowView, state, verifier, "/app");

      const url = new URL(`${getExampleAuthServerUrl()}/authorize`);
      url.searchParams.set("response_type", "code");
      url.searchParams.set("client_id", getExampleClientId());
      url.searchParams.set("redirect_uri", getExampleRedirectUri());
      url.searchParams.set("state", state);
      url.searchParams.set("code_challenge", challenge);
      url.searchParams.set("code_challenge_method", "S256");
      if (flowView === "signup") {
        url.searchParams.set("view", "signup");
      }

      window.location.replace(url.toString());
    } catch (error) {
      setErr(error instanceof Error ? error.message : "Failed to start headless auth.");
      setStarting(false);
    }
  };

  return (
    <div className="headless-auth-starter">
      <div className="headless-auth-starter-copy">
        <p className="muted">
          This page is the app-owned authorize surface. Starting the flow here still sends the browser
          through SqlOS <code>/authorize</code>, then comes back with a real authorization request.
        </p>
      </div>
      {err ? <p className="headless-auth-error-banner">{err}</p> : null}
      <div className="actions">
        <button onClick={() => void startFlow("login")} disabled={starting}>
          {starting && selectedView === "login" ? "Opening authorize flow..." : "Start headless sign in"}
        </button>
        <button className="secondary" onClick={() => void startFlow("signup")} disabled={starting}>
          {starting && selectedView === "signup" ? "Opening signup flow..." : "Start headless signup"}
        </button>
      </div>
    </div>
  );
}
