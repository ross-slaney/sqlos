import Link from "next/link";

export const dynamic = "force-dynamic";

export default function AuthCallbackPage() {
  return (
    <div className="callback-page">
      <div className="callback-card">
        <h2>This callback moved</h2>
        <p>
          Hosted sign-in now finishes at the Auth.js route{" "}
          <code>/api/auth/callback/sqlos</code>. Start again from Sign in so the
          library can own PKCE, the code exchange, and the session.
        </p>
        <p>
          <Link href="/">Return to Sign in</Link>
        </p>
      </div>
    </div>
  );
}
