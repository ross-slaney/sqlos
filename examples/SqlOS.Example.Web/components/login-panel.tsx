"use client";

import { signIn } from "next-auth/react";
import { useRouter } from "next/navigation";
import { FormEvent, useEffect, useMemo, useState } from "react";

type DiscoveryResult = {
  mode: "password" | "sso";
  organizationName?: string | null;
  primaryDomain?: string | null;
};

type OidcProvider = {
  connectionId: string;
  providerType: "Google" | "Microsoft" | "Apple" | "Custom";
  displayName: string;
};

const apiUrl = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5062";
const authServerUrl = `${apiUrl}/sqlos/auth`;
const exampleClientId = process.env.NEXT_PUBLIC_SQL_OS_CLIENT_ID ?? "example-web";
const authFlowStorageKey = "sqlos_example_auth_flow";
const oidcStateStorageKey = "sqlos_example_oidc_state";
const oidcVerifierStorageKey = "sqlos_example_oidc_verifier";
const oidcRedirectUriStorageKey = "sqlos_example_oidc_redirect_uri";
const oidcClientIdStorageKey = "sqlos_example_oidc_client_id";

function encodeBase64Url(bytes: Uint8Array) {
  let binary = "";
  bytes.forEach((value) => {
    binary += String.fromCharCode(value);
  });

  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function createOpaqueToken(size = 32) {
  const bytes = new Uint8Array(size);
  crypto.getRandomValues(bytes);
  return encodeBase64Url(bytes);
}

async function createCodeChallenge(verifier: string) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));
  return encodeBase64Url(new Uint8Array(digest));
}

export function LoginPanel() {
  const router = useRouter();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [mode, setMode] = useState<"email" | "password">("email");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [providers, setProviders] = useState<OidcProvider[]>([]);
  const [providerLoading, setProviderLoading] = useState(true);

  const canSubmitEmail = useMemo(() => email.trim().length > 3, [email]);

  useEffect(() => {
    let cancelled = false;

    async function loadProviders() {
      try {
        const response = await fetch(`${authServerUrl}/oidc/providers`);
        const data = (await response.json()) as OidcProvider[];
        if (!response.ok) {
          throw new Error("Failed to load OIDC providers.");
        }

        if (!cancelled) {
          setProviders(data);
        }
      } catch {
        if (!cancelled) {
          setProviders([]);
        }
      } finally {
        if (!cancelled) {
          setProviderLoading(false);
        }
      }
    }

    void loadProviders();
    return () => {
      cancelled = true;
    };
  }, []);

  async function handleDiscover(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setLoading(true);
    setError(null);
    setMessage(null);

    try {
      const response = await fetch(`${apiUrl}/api/v1/auth/discover`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email })
      });

      const data: DiscoveryResult | { message?: string } = await response.json();
      if (!response.ok) {
        throw new Error("message" in data && data.message ? data.message : "Email discovery failed.");
      }

      if ("mode" in data && data.mode === "sso") {
        setMessage(`Using SSO for ${data.organizationName ?? data.primaryDomain ?? email}. Redirecting now...`);
        const startResponse = await fetch(`${apiUrl}/api/v1/auth/sso/start`, {
          method: "POST",
          credentials: "include",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ email })
        });
        const startData = await startResponse.json();
        if (!startResponse.ok) {
          throw new Error(startData.message || "Failed to start SSO.");
        }

        sessionStorage.setItem(authFlowStorageKey, "sso");
        window.location.href = startData.authorizationUrl;
        return;
      }

      setMode("password");
      setMessage("Password login is enabled for this email.");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Email discovery failed.");
    } finally {
      setLoading(false);
    }
  }

  async function handlePasswordLogin(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setLoading(true);
    setError(null);
    setMessage(null);

    try {
      const result = await signIn("credentials", {
        redirect: false,
        email,
        password
      });

      if (!result || result.error) {
        throw new Error(result?.error || "Login failed.");
      }

      router.replace("/app");
      router.refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Login failed.");
    } finally {
      setLoading(false);
    }
  }

  async function handleOidcLogin(provider: OidcProvider) {
    setLoading(true);
    setError(null);
    setMessage(null);

    try {
      const state = createOpaqueToken();
      const codeVerifier = createOpaqueToken(48);
      const redirectUri = `${window.location.origin}/auth/callback`;
      const response = await fetch(`${authServerUrl}/oidc/authorization-url`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          connectionId: provider.connectionId,
          clientId: exampleClientId,
          redirectUri,
          state,
          codeChallenge: await createCodeChallenge(codeVerifier),
          codeChallengeMethod: "S256",
          email: email.trim() || null
        })
      });

      const data = await response.json();
      if (!response.ok) {
        throw new Error(data.message || "Failed to start OIDC sign-in.");
      }

      sessionStorage.setItem(authFlowStorageKey, "oidc");
      sessionStorage.setItem(oidcStateStorageKey, state);
      sessionStorage.setItem(oidcVerifierStorageKey, codeVerifier);
      sessionStorage.setItem(oidcRedirectUriStorageKey, redirectUri);
      sessionStorage.setItem(oidcClientIdStorageKey, exampleClientId);
      setMessage(`Redirecting to ${provider.displayName}...`);
      window.location.href = data.authorizationUrl;
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to start OIDC sign-in.");
      setLoading(false);
    }
  }

  function renderOidcButtons() {
    if (providerLoading) {
      return <p className="muted">Loading OIDC providers...</p>;
    }

    if (providers.length === 0) {
      return <p className="muted">No OIDC providers are configured for this example.</p>;
    }

    return (
      <div className="stacked-actions">
        {providers.map((provider) => (
          <button
            key={provider.connectionId}
            className="secondary"
            disabled={loading}
            type="button"
            onClick={() => void handleOidcLogin(provider)}
          >
            {loading ? "Starting..." : `Continue with ${provider.displayName}`}
          </button>
        ))}
      </div>
    );
  }

  return (
    <div className="card">
      <h2>Sign in</h2>
      <p className="muted">
        Enter your email first. If SqlOS finds an SSO organization for the domain, that SSO flow
        wins and the password/OIDC options are bypassed automatically.
      </p>

      {mode === "email" ? (
        <form onSubmit={handleDiscover}>
          <input
            autoComplete="email"
            name="email"
            placeholder="name@example.com"
            type="email"
            value={email}
            onChange={(event) => setEmail(event.target.value)}
            required
          />
          <button disabled={!canSubmitEmail || loading} type="submit">
            {loading ? "Checking..." : "Continue"}
          </button>
        </form>
      ) : (
        <>
          <form onSubmit={handlePasswordLogin}>
            <input name="email" value={email} readOnly />
            <input
              autoComplete="current-password"
              name="password"
              placeholder="Password"
              type="password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              required
            />
            <div className="actions">
              <button disabled={loading} type="submit">
                {loading ? "Signing in..." : "Sign in"}
              </button>
              <button
                className="secondary"
                disabled={loading}
                type="button"
                onClick={() => {
                  setMode("email");
                  setPassword("");
                  setMessage(null);
                  setError(null);
                }}
              >
                Change email
              </button>
            </div>
          </form>
          <div className="separator">or continue with</div>
          {renderOidcButtons()}
        </>
      )}

      {mode === "email" ? (
        <>
          <div className="separator">or continue with</div>
          {renderOidcButtons()}
        </>
      ) : null}

      {message ? <p className="success">{message}</p> : null}
      {error ? <p className="error">{error}</p> : null}
    </div>
  );
}
