"use client";

import { useState } from "react";

type TokenDebugResponse = {
  refreshRequired: boolean;
  previousExpiresAt: string | null;
  previousSecondsRemaining: number | null;
  currentExpiresAt: string | null;
  sessionId: string | null;
  organizationId: string | null;
  accessToken: string;
  decodedAccessToken: Record<string, unknown>;
  refreshTokenExposed: boolean;
  sessionError: string | null;
};

export function TokenDebugPanel() {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<TokenDebugResponse | null>(null);

  async function handleClick() {
    setLoading(true);
    setError(null);

    try {
      const response = await fetch("/api/example/token", {
        method: "GET",
        cache: "no-store"
      });

      const data = await response.json();
      if (!response.ok) {
        throw new Error(data.message || "Failed to retrieve the current token.");
      }

      setResult(data as TokenDebugResponse);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to retrieve the current token.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <section className="card">
      <h2>Token debug</h2>
      <p className="muted">
        Fetch the current access token from the server-side session. If the token is near expiry, the
        server will refresh it first and log what happened.
      </p>
      <div className="actions">
        <button disabled={loading} type="button" onClick={handleClick}>
          {loading ? "Checking token..." : "Get token"}
        </button>
      </div>
      {error ? <p className="error">{error}</p> : null}
      {result ? <pre>{JSON.stringify(result, null, 2)}</pre> : null}
    </section>
  );
}
