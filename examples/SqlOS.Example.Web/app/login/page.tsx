import { Suspense } from "react";
import { SqlOSAuthRedirect } from "@/components/sqlos-auth-redirect";

export default async function LoginPage() {
  return (
    <div className="callback-page">
      <div className="callback-card">
        <h2>Redirecting to sign in...</h2>
        <p>Taking you to the SqlOS hosted auth page.</p>
        <Suspense fallback={<p className="muted">Preparing...</p>}>
          <SqlOSAuthRedirect view="login" />
        </Suspense>
      </div>
    </div>
  );
}
