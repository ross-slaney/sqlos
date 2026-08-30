import { Suspense } from "react";
import { SqlOSHostedSignIn } from "@/components/sqlos-hosted-sign-in";

export default async function LoginPage() {
  return (
    <div className="callback-page">
      <div className="callback-card">
        <h2>Redirecting to sign in...</h2>
        <p>Auth.js is starting the standard OpenID Connect authorization-code flow.</p>
        <Suspense fallback={<p className="muted">Preparing...</p>}>
          <SqlOSHostedSignIn view="login" />
        </Suspense>
      </div>
    </div>
  );
}
