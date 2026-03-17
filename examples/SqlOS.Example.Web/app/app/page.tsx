import { AuthorizedApiPanel } from "@/components/authorized-api-panel";
import { ExampleProfilePanel } from "@/components/example-profile-panel";
import { LogoutButton } from "@/components/logout-button";

export default function AppPage() {
  return (
    <main className="shell">
      <div className="stack">
        <section className="hero">
          <h1>Authenticated App</h1>
          <p>
            This page shows the payoff from the headless demo. The access token in NextAuth is the
            same SqlOS-issued token you would use for your normal product API, and the referral
            source card below proves your app can persist custom signup data without owning OAuth.
          </p>
          <div className="actions">
            <LogoutButton />
            <a className="button secondary" href="http://localhost:5062/sqlos/">
              Back to SqlOS dashboard
            </a>
          </div>
        </section>

        <div className="grid two">
          <ExampleProfilePanel />
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
