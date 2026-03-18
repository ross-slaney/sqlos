import { Suspense } from "react";
import { SqlOSHeadlessAuthPanel } from "@/components/sqlos-headless-auth-panel";

export const dynamic = "force-dynamic";

export default function HeadlessAuthorizePage() {
  return (
    <Suspense fallback={
      <div className="ha">
        <div className="ha-left">
          <div className="ha-left-overlay" />
        </div>
        <div className="ha-right">
          <p className="muted">Loading...</p>
        </div>
      </div>
    }>
      <SqlOSHeadlessAuthPanel />
    </Suspense>
  );
}
