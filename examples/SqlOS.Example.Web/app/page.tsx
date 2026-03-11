import Link from "next/link";

export default function HomePage() {
  return (
    <main className="shell">
      <div className="stack">
        <section className="hero">
          <h1>SqlOS Example Web</h1>
          <p>
            This frontend talks only to the example backend. The backend embeds SqlOS and handles
            local auth, OIDC login, SAML PKCE exchange, refresh, logout, and session inspection.
          </p>
          <div className="actions">
            <Link className="button" href="/login">
              Go to login
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
