"use client";

import { signIn, signOut } from "next-auth/react";

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
  return (
    <button
      style={{ ...buttonStyle, background: "#fff", color: "#111827" }}
      onClick={() => signOut()}
    >
      Sign out
    </button>
  );
}
