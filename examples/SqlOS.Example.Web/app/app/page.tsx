import { jwtDecode } from "jwt-decode";
import { getServerSession } from "next-auth";
import { redirect } from "next/navigation";
import { ClientApiDebugPanel } from "@/components/client-api-debug-panel";
import { LogoutButton } from "@/components/logout-button";
import { ServerActionDebugPanel } from "@/components/server-action-debug-panel";
import { TokenDebugPanel } from "@/components/token-debug-panel";
import { authOptions } from "@/lib/auth";

const apiUrl = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5062";

export default async function AppPage() {
  const session = await getServerSession(authOptions);
  if (!session?.user || !session.accessToken) {
    redirect("/login");
  }

  const debugResponse = await fetch(`${apiUrl}/api/v1/auth/session`, {
    headers: {
      Authorization: `Bearer ${session.accessToken}`
    },
    cache: "no-store"
  });

  const debugJson = debugResponse.ok
    ? await debugResponse.json()
    : { error: `Backend session lookup failed with ${debugResponse.status}.` };

  const decodedToken = jwtDecode<Record<string, unknown>>(session.accessToken);

  return (
    <main className="shell">
      <div className="stack">
        <section className="hero">
          <h1>Authenticated App</h1>
          <p>
            This page is protected by NextAuth and shows the current browser session, decoded access
            token claims, and the backend session record resolved through the example API.
          </p>
          <div className="actions">
            <LogoutButton />
            <a className="button secondary" href="http://localhost:5062/sqlos/">
              Back to SqlOS dashboard
            </a>
          </div>
        </section>

        <div className="grid two">
          <section className="card">
            <h2>NextAuth session</h2>
            <pre>{JSON.stringify(session, null, 2)}</pre>
          </section>
          <section className="card">
            <h2>Decoded access token</h2>
            <pre>{JSON.stringify(decodedToken, null, 2)}</pre>
          </section>
        </div>

        <section className="card">
          <h2>Backend session debug</h2>
          <pre>{JSON.stringify(debugJson, null, 2)}</pre>
        </section>

        <TokenDebugPanel />

        <div className="grid two">
          <ClientApiDebugPanel />
          <ServerActionDebugPanel />
        </div>
      </div>
    </main>
  );
}
