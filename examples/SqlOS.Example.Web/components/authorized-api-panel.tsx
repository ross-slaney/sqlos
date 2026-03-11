"use client";

import { useSession } from "next-auth/react";
import { useState } from "react";

const apiUrl = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5062";

export function AuthorizedApiPanel() {
  const { data: session, status } = useSession();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<unknown>(null);

  async function handleClick() {
    if (!session?.accessToken) {
      setError("No authenticated session is available on the client.");
      return;
    }

    setLoading(true);
    setError(null);

    try {
      const response = await fetch(`${apiUrl}/api/hello`, {
        headers: {
          Authorization: `Bearer ${session.accessToken}`
        },
        cache: "no-store"
      });

      const payload = await response.json().catch(() => null);
      if (!response.ok) {
        throw new Error(`Authorized request failed with ${response.status}.`);
      }

      setResult({
        ok: true,
        sessionId: session.sessionId,
        organizationId: session.organizationId,
        sessionError: session.error ?? null,
        payload
      });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Authorized request failed.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <section className="card">
      <h2>Authorized API request</h2>
      <p className="muted">
        This button uses the access token already present in the client session owned by NextAuth's
        <code> SessionProvider</code>. Refresh happens through the provider's normal session fetch
        path, not through an extra manual session request from this component.
      </p>
      <div className="actions">
        <button disabled={loading || status !== "authenticated"} type="button" onClick={() => void handleClick()}>
          {loading ? "Calling API..." : "Call authorized API"}
        </button>
      </div>
      {error ? <p className="error">{error}</p> : null}
      {result ? <pre>{JSON.stringify(result, null, 2)}</pre> : null}
    </section>
  );
}
