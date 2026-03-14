import Link from "next/link";

export default function HomePage() {
  return (
    <main className="shell">
      <div className="stack">
        <section className="hero">
          <h1>SqlOS Example Web</h1>
          <p>
            This frontend talks only to the example backend. The backend embeds SqlOS and now uses
            the hosted SqlOS AuthPage for browser sign-in, signup, PKCE code exchange, refresh,
            logout, and session inspection.
          </p>
          <div className="actions">
            <Link className="button" href="/retail">
              Open Retail Demo
            </Link>
            <Link className="button secondary" href="/login">
              Open hosted sign in
            </Link>
            <Link className="button secondary" href="/signup">
              Open hosted signup
            </Link>
            <a className="button secondary" href="http://localhost:5062/sqlos/">
              Open SqlOS dashboard
            </a>
          </div>
        </section>
      </div>
    </main>
  );
}
