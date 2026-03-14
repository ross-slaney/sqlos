import { Suspense } from "react";
import { SsoCallbackPanel } from "@/components/sso-callback-panel";

export const dynamic = "force-dynamic";

export default function AuthCallbackPage() {
  return (
    <main className="shell">
      <div className="stack">
        <section className="hero">
          <h1>Auth callback</h1>
          <Suspense fallback={<p>Completing sign-in...</p>}>
            <SsoCallbackPanel />
          </Suspense>
        </section>
      </div>
    </main>
  );
}
