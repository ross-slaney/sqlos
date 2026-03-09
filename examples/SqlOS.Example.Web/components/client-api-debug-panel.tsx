"use client";

import { useSession } from "next-auth/react";
import { useState } from "react";

const apiUrl = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5062";

export function ClientApiDebugPanel() {
  const { data: session } = useSession();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<unknown>(null);

  async function handleClick() {
    if (!session?.accessToken) {
      setError("No access token is available in the client session.");
      return;
    }

    setLoading(true);
    setError(null);

    try {
      const response = await fetch(`${apiUrl}/api/v1/auth/session`, {
        headers: {
          Authorization: `Bearer ${session.accessToken}`
        },
        cache: "no-store"
      });

      const data = await response.json().catch(() => null);
      if (!response.ok) {
        throw new Error(`Client request failed with ${response.status}.`);
      }

      setResult({
        request: "client-side fetch",
        usedSessionId: session.sessionId,
        usedOrganizationId: session.organizationId,
        payload: data
      });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Client request failed.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <section className="card">
      <h2>Client-side API request</h2>
      <p className="muted">
        This button uses <code>useSession()</code> on the client, reads the exposed access token, and
        calls the example API directly from the browser.
      </p>
      <div className="actions">
        <button disabled={loading} type="button" onClick={handleClick}>
          {loading ? "Calling API..." : "Call API from client"}
        </button>
      </div>
      {error ? <p className="error">{error}</p> : null}
      {result ? <pre>{JSON.stringify(result, null, 2)}</pre> : null}
    </section>
  );
}
