import { Suspense } from "react";
import { SqlOSAuthCallbackPanel } from "@/components/sqlos-auth-callback-panel";

export const dynamic = "force-dynamic";

export default function AuthCallbackPage() {
  return (
    <div className="callback-page">
      <div className="callback-card">
        <h2>Completing sign in...</h2>
        <Suspense fallback={<p className="muted">Processing...</p>}>
          <SqlOSAuthCallbackPanel />
        </Suspense>
      </div>
    </div>
  );
}
