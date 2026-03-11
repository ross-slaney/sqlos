import { AuthorizedApiPanel } from "@/components/authorized-api-panel";
import { LogoutButton } from "@/components/logout-button";

export default function AppPage() {
  return (
    <main className="shell">
      <div className="stack">
        <section className="hero">
          <h1>Authenticated App</h1>
          <p>
            This page is protected by NextAuth. Use the button below to make a normal authenticated
            request to the example API and verify that refresh and sign-out behavior are working.
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
            <h2>Validation goal</h2>
            <pre>{JSON.stringify({
              endpoint: "/api/hello",
              expectedResponse: "hello",
              accessTokenSource: "next-auth client session",
              refreshBehavior: "automatic through SessionProvider and /api/auth/session"
            }, null, 2)}</pre>
          </section>
          <section className="card">
            <h2>Expected auth behavior</h2>
            <pre>{JSON.stringify({
              redirectIfSignedOut: "/login",
              automaticRefresh: true,
              signOutOnRefreshFailure: true
            }, null, 2)}</pre>
          </section>
        </div>

        <AuthorizedApiPanel />
      </div>
    </main>
  );
}
