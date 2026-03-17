"use client";

import { useEffect, useState, useCallback } from "react";
import { useSearchParams } from "next/navigation";
import { signIn } from "next-auth/react";
import { jwtDecode } from "jwt-decode";
import {
  getHeadlessRequest,
  headlessIdentify,
  headlessPasswordLogin,
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

  // Form state
  const [email, setEmail] = useState(initialEmail);
  const [password, setPassword] = useState("");
  const [displayName, setDisplayName] = useState(initialDisplayName);
  const [orgName, setOrgName] = useState("");
  const [firstName, setFirstName] = useState("");
  const [lastName, setLastName] = useState("");

  // Load the headless request on mount
  useEffect(() => {
    if (!requestId) return;

    const load = async () => {
      try {
        const vm = await getHeadlessRequest(requestId, initialView, initialError, pendingToken, initialEmail, initialDisplayName);
        setViewModel(vm);
        setSettings(vm.settings ?? null);
        if (vm.view) setView(vm.view);
        if (vm.error) setError(vm.error);
        if (vm.email) setEmail(vm.email);
        if (vm.fieldErrors) setFieldErrors(vm.fieldErrors);
      } catch (err) {
        setError(err instanceof Error ? err.message : "Failed to load authorization request.");
      }
    };

    void load();
  }, [requestId, initialView, initialError, pendingToken, initialEmail, initialDisplayName]);

  const handleResult = useCallback(async (result: HeadlessActionResult) => {
    if (result.type === "redirect" && result.redirectUrl) {
      // The redirect URL contains the authorization code — exchange it for tokens
      const url = new URL(result.redirectUrl);
      const code = url.searchParams.get("code");
      const state = url.searchParams.get("state");

      if (code) {
        // We need to exchange the code via the token endpoint
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

      // Fallback: just redirect (e.g. for OIDC provider starts)
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

  const onIdentify = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!requestId) return;
    setLoading(true);
    setError(null);
    setFieldErrors({});
    try {
      const result = await headlessIdentify(requestId, email);
      await handleResult(result);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Identify failed.");
    } finally {
      setLoading(false);
    }
  };

  const onLogin = async (e: React.FormEvent) => {
    e.preventDefault();
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

  const onSignup = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!requestId) return;
    setLoading(true);
    setError(null);
    setFieldErrors({});
    try {
      const result = await headlessSignup(requestId, displayName, email, password, orgName, {
        firstName,
        lastName,
        companyName: orgName,
      });
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

  // No request ID — show the PKCE initiation (start a headless authorize flow)
  if (!requestId) {
    return <HeadlessFlowStarter />;
  }

  const primaryColor = settings?.primaryColor || "#2563eb";

  return (
    <div>
      {error && <p className="error">{error}</p>}

      {view === "login" && (
        <form onSubmit={onLogin}>
          <label htmlFor="headless-email">Email</label>
          <input
            id="headless-email"
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            placeholder="you@example.com"
            required
          />
          {fieldErrors.email && <p className="error">{fieldErrors.email}</p>}

          <label htmlFor="headless-password">Password</label>
          <input
            id="headless-password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            placeholder="Password"
            required
          />
          {fieldErrors.password && <p className="error">{fieldErrors.password}</p>}

          <button type="submit" disabled={loading} style={{ background: primaryColor }}>
            {loading ? "Signing in..." : "Sign in"}
          </button>

          <div className="separator">or</div>
          <button type="button" className="secondary" onClick={() => setView("signup")}>
            Create an account
          </button>
        </form>
      )}

      {view === "signup" && (
        <form onSubmit={onSignup}>
          <label htmlFor="headless-signup-name">Display Name</label>
          <input
            id="headless-signup-name"
            type="text"
            value={displayName}
            onChange={(e) => setDisplayName(e.target.value)}
            placeholder="Jane Doe"
            required
          />

          <label htmlFor="headless-signup-email">Email</label>
          <input
            id="headless-signup-email"
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            placeholder="you@example.com"
            required
          />
          {fieldErrors.email && <p className="error">{fieldErrors.email}</p>}

          <label htmlFor="headless-signup-password">Password</label>
          <input
            id="headless-signup-password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            placeholder="Choose a password"
            required
          />
          {fieldErrors.password && <p className="error">{fieldErrors.password}</p>}

          <label htmlFor="headless-signup-org">Organization Name</label>
          <input
            id="headless-signup-org"
            type="text"
            value={orgName}
            onChange={(e) => setOrgName(e.target.value)}
            placeholder="Acme Inc."
            required
          />
          {fieldErrors.organizationName && <p className="error">{fieldErrors.organizationName}</p>}

          <label htmlFor="headless-signup-first">First Name</label>
          <input
            id="headless-signup-first"
            type="text"
            value={firstName}
            onChange={(e) => setFirstName(e.target.value)}
            placeholder="Jane"
          />
          {fieldErrors.firstName && <p className="error">{fieldErrors.firstName}</p>}

          <label htmlFor="headless-signup-last">Last Name</label>
          <input
            id="headless-signup-last"
            type="text"
            value={lastName}
            onChange={(e) => setLastName(e.target.value)}
            placeholder="Doe"
          />
          {fieldErrors.lastName && <p className="error">{fieldErrors.lastName}</p>}

          <button type="submit" disabled={loading} style={{ background: primaryColor }}>
            {loading ? "Creating account..." : "Create Account"}
          </button>

          <div className="separator">or</div>
          <button type="button" className="secondary" onClick={() => setView("login")}>
            Already have an account?
          </button>
        </form>
      )}

      {view === "password" && (
        <form onSubmit={onLogin}>
          <p className="muted">Enter your password for {email}</p>
          <label htmlFor="headless-pw">Password</label>
          <input
            id="headless-pw"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            placeholder="Password"
            required
          />
          <button type="submit" disabled={loading} style={{ background: primaryColor }}>
            {loading ? "Signing in..." : "Sign in"}
          </button>
        </form>
      )}

      {view === "identify" && (
        <form onSubmit={onIdentify}>
          <label htmlFor="headless-id-email">Email</label>
          <input
            id="headless-id-email"
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            placeholder="you@example.com"
            required
          />
          <button type="submit" disabled={loading} style={{ background: primaryColor }}>
            {loading ? "Looking up..." : "Continue"}
          </button>
        </form>
      )}

      {/* OIDC / SAML providers */}
      {viewModel?.providers && viewModel.providers.length > 0 && (
        <div style={{ marginTop: "1rem" }}>
          <div className="separator">External providers</div>
          <div className="stacked-actions">
            {viewModel.providers.map((provider) => (
              <button
                key={provider.connectionId}
                type="button"
                className="secondary"
                disabled={loading}
                onClick={() => onProviderStart(provider.connectionId)}
              >
                Continue with {provider.displayName}
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

/** Initiates a PKCE authorize flow that lands on the headless UI. */
function HeadlessFlowStarter() {
  const [starting, setStarting] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  const startFlow = async (flowView: "login" | "signup") => {
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
    } catch (e) {
      setErr(e instanceof Error ? e.message : "Failed to start headless flow.");
      setStarting(false);
    }
  };

  return (
    <div>
      <p className="muted">
        Start an OAuth PKCE flow. SqlOS will redirect back here with the headless authorization request.
      </p>
      {err && <p className="error">{err}</p>}
      <div className="actions">
        <button onClick={() => startFlow("login")} disabled={starting}>
          {starting ? "Starting..." : "Sign in"}
        </button>
        <button className="secondary" onClick={() => startFlow("signup")} disabled={starting}>
          Sign up
        </button>
      </div>
    </div>
  );
}
