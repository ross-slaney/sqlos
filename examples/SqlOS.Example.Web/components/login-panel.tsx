"use client";

import { signIn } from "next-auth/react";
import { useRouter } from "next/navigation";
import { FormEvent, useMemo, useState } from "react";

type DiscoveryResult = {
  mode: "password" | "sso";
  organizationName?: string | null;
  primaryDomain?: string | null;
};

const apiUrl = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5062";

export function LoginPanel() {
  const router = useRouter();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [mode, setMode] = useState<"email" | "password">("email");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const canSubmitEmail = useMemo(() => email.trim().length > 3, [email]);

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

  return (
    <div className="card">
      <h2>Sign in</h2>
      <p className="muted">
        Enter your email first. If SqlOS finds an SSO organization for the domain, the password step
        is skipped and the SAML flow begins immediately.
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
      )}

      {message ? <p className="success">{message}</p> : null}
      {error ? <p className="error">{error}</p> : null}
    </div>
  );
}
