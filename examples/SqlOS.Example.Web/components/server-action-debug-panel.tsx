"use client";

import { useState, useTransition } from "react";
import { fetchBackendSessionFromServerAction } from "@/app/app/actions";

export function ServerActionDebugPanel() {
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<unknown>(null);

  function handleClick() {
    setError(null);

    startTransition(async () => {
      try {
        const response = await fetchBackendSessionFromServerAction();
        if (!response.ok) {
          throw new Error(response.message ?? `Server action failed with ${response.status ?? 500}.`);
        }

        setResult({
          request: "server action",
          ...response
        });
      } catch (err) {
        setError(err instanceof Error ? err.message : "Server action failed.");
      }
    });
  }

  return (
    <section className="card">
      <h2>Server action API request</h2>
      <p className="muted">
        This button runs a server action, resolves the NextAuth session on the server with{" "}
        <code>getServerSession()</code>, and calls the example API server-side.
      </p>
      <div className="actions">
        <button disabled={isPending} type="button" onClick={handleClick}>
          {isPending ? "Calling API..." : "Call API from server action"}
        </button>
      </div>
      {error ? <p className="error">{error}</p> : null}
      {result ? <pre>{JSON.stringify(result, null, 2)}</pre> : null}
    </section>
  );
}
