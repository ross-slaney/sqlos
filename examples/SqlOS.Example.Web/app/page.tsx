"use client";

import { Suspense, useState } from "react";
import { useSession } from "next-auth/react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import {
  getExampleAuthServerUrl,
  getExampleClientId,
  getExampleRedirectUri,
  createOpaqueToken,
  createCodeChallenge,
  persistSqlOSAuthFlow,
} from "@/lib/sqlos-auth";

export default function LandingPage() {
  return (
    <Suspense>
      <LandingContent />
    </Suspense>
  );
}

function LandingContent() {
  const { data: session } = useSession();
  const searchParams = useSearchParams();
  const next = searchParams.get("next") || "/retail";
  const [starting, setStarting] = useState<"login" | "signup" | null>(null);

  if (session) {
    return (
      <div className="landing">
        <div className="landing-card">
          <div className="landing-badge">Powered by SqlOS</div>
          <h1>Welcome back</h1>
          <p>
            Signed in as <strong>{session.user?.name ?? session.user?.email}</strong>.
          </p>
          <div className="landing-actions">
            <Link href="/retail" className="button">
              Go to Dashboard
            </Link>
          </div>
        </div>
      </div>
    );
  }

  async function startAuth(view: "login" | "signup") {
    setStarting(view);
    try {
      const verifier = createOpaqueToken(48);
      const state = createOpaqueToken(24);
      const challenge = await createCodeChallenge(verifier);

      persistSqlOSAuthFlow(view, state, verifier, next);

      const url = new URL(`${getExampleAuthServerUrl()}/authorize`);
      url.searchParams.set("response_type", "code");
      url.searchParams.set("client_id", getExampleClientId());
      url.searchParams.set("redirect_uri", getExampleRedirectUri());
      url.searchParams.set("state", state);
      url.searchParams.set("code_challenge", challenge);
      url.searchParams.set("code_challenge_method", "S256");
      if (view === "signup") {
        url.searchParams.set("view", "signup");
      }

      window.location.replace(url.toString());
    } catch {
      setStarting(null);
    }
  }

  return (
    <div className="landing">
      <div className="landing-card">
        <div className="landing-badge">Powered by SqlOS</div>
        <h1>Northwind Retail</h1>
        <p>
          Manage chains, stores, and inventory — with fine-grained access
          control. Sign in to try it out.
        </p>
        <div className="landing-actions">
          <button
            type="button"
            disabled={starting !== null}
            onClick={() => void startAuth("login")}
          >
            {starting === "login" ? "Redirecting..." : "Sign In"}
          </button>
          <button
            type="button"
            className="secondary"
            disabled={starting !== null}
            onClick={() => void startAuth("signup")}
          >
            {starting === "signup" ? "Redirecting..." : "Sign Up"}
          </button>
        </div>
        <div className="landing-features">
          <div className="landing-feature">
            <strong>Chains</strong>
            <p>Organize retail chains and manage their details.</p>
          </div>
          <div className="landing-feature">
            <strong>Stores</strong>
            <p>Track locations across regions and chains.</p>
          </div>
          <div className="landing-feature">
            <strong>Inventory</strong>
            <p>Add, edit, and manage stock at each location.</p>
          </div>
          <div className="landing-feature">
            <strong>Permissions</strong>
            <p>See only what you're authorized to — powered by FGA.</p>
          </div>
        </div>
        <div className="landing-footer">
          <a href="http://localhost:5062/sqlos/">Open SqlOS Dashboard</a>
        </div>
      </div>
    </div>
  );
}
