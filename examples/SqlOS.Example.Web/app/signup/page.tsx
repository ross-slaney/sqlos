import { Suspense } from "react";
import { SqlOSAuthRedirect } from "@/components/sqlos-auth-redirect";

export default function SignupPage() {
  return (
    <main className="shell">
      <div className="stack">
        <section className="hero">
          <h1>Hosted SqlOS signup</h1>
          <p>
            Account creation is also handled by the hosted SqlOS AuthPage, so this example uses the
            same PKCE browser flow for both sign in and sign up.
          </p>
        </section>
        <Suspense fallback={<section className="card"><p className="muted">Preparing hosted signup...</p></section>}>
          <SqlOSAuthRedirect view="signup" />
        </Suspense>
      </div>
    </main>
  );
}
