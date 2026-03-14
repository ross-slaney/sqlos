import { Suspense } from "react";
import { SqlOSAuthRedirect } from "@/components/sqlos-auth-redirect";

export default async function LoginPage() {
  return (
    <main className="shell">
      <div className="stack">
        <section className="hero">
          <h1>Hosted SqlOS sign in</h1>
          <p>
            This example app now delegates browser sign-in to the hosted SqlOS AuthPage. SqlOS
            handles password signup, home realm discovery, OIDC social login, and SAML redirects
            behind one standards-based authorization surface.
          </p>
        </section>
        <Suspense fallback={<section className="card"><p className="muted">Preparing hosted sign in...</p></section>}>
          <SqlOSAuthRedirect view="login" />
        </Suspense>
      </div>
    </main>
  );
}
