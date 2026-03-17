import { Suspense } from "react";
import { SqlOSHeadlessAuthPanel } from "@/components/sqlos-headless-auth-panel";

export const dynamic = "force-dynamic";

export default function HeadlessAuthorizePage() {
  return (
    <main className="shell">
      <div className="stack">
        <section className="hero">
          <h1>Headless SqlOS auth</h1>
          <p>
            This page is your app&apos;s own login and signup UI. SqlOS handles the OAuth protocol,
            sessions, and token issuance behind the scenes while your frontend owns the full
            user experience.
          </p>
        </section>
        <section className="card">
          <Suspense fallback={<p className="muted">Loading headless auth...</p>}>
            <SqlOSHeadlessAuthPanel />
          </Suspense>
        </section>
      </div>
    </main>
  );
}
