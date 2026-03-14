import { Suspense } from "react";
import { SqlOSAuthCallbackPanel } from "@/components/sqlos-auth-callback-panel";

export const dynamic = "force-dynamic";

export default function AuthCallbackPage() {
  return (
    <main className="shell">
      <div className="stack">
        <section className="hero">
          <h1>Hosted SqlOS callback</h1>
          <Suspense fallback={<p>Completing sign-in...</p>}>
            <SqlOSAuthCallbackPanel />
          </Suspense>
        </section>
      </div>
    </main>
  );
}
