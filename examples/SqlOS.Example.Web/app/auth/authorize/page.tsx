import { Suspense } from "react";
import { SqlOSHeadlessAuthPanel } from "@/components/sqlos-headless-auth-panel";

export const dynamic = "force-dynamic";

export default function HeadlessAuthorizePage() {
  return (
    <main className="headless-auth-page">
      <Suspense fallback={<div className="shell"><section className="card"><p className="muted">Loading headless auth...</p></section></div>}>
        <SqlOSHeadlessAuthPanel />
      </Suspense>
    </main>
  );
}
