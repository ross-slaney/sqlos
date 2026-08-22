"use client";

import { signIn, signOut } from "next-auth/react";

const xOrigin = process.env.NEXT_PUBLIC_SQLOS_ORIGIN ?? "http://localhost:5100";

const buttonStyle: React.CSSProperties = {
  padding: "0.6rem 1.2rem",
  borderRadius: "0.5rem",
  border: "1px solid #111827",
  background: "#111827",
  color: "#fff",
  fontSize: "1rem",
  cursor: "pointer"
};

export function SignInWithX() {
  return (
    <button style={buttonStyle} onClick={() => signIn("sqlos")}>
      Sign in with X
    </button>
  );
}

export function SignOut() {
  // Federated sign-out: end App Y's session, then X's browser session, and
  // land back here signed out — so the next "Sign in with X" really
  // re-authenticates. Signing out of App Y alone would leave X's session
  // alive and the next sign-in silent (the standard OIDC two-layer model;
  // spec-shaped RP-initiated logout is SqlOS issue #266).
  return (
    <button
      style={{ ...buttonStyle, background: "#fff", color: "#111827" }}
      onClick={async () => {
        await signOut({ redirect: false });
        window.location.href = `${xOrigin}/sqlos/auth/logout?returnTo=${encodeURIComponent(window.location.origin)}`;
      }}
    >
      Sign out
    </button>
  );
}
