import { Suspense } from "react";
import { SqlOSAuthRedirect } from "@/components/sqlos-auth-redirect";

export default function SignupPage() {
  return (
    <div className="callback-page">
      <div className="callback-card">
        <h2>Redirecting to sign up...</h2>
        <p>Taking you to the SqlOS hosted auth page.</p>
        <Suspense fallback={<p className="muted">Preparing...</p>}>
          <SqlOSAuthRedirect view="signup" />
        </Suspense>
      </div>
    </div>
  );
}
