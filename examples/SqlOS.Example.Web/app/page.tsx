import Link from "next/link";
import { getServerSession } from "next-auth";
import { redirect } from "next/navigation";
import { authOptions } from "@/lib/auth";

export default async function HomePage() {
  const session = await getServerSession(authOptions);
  if (session?.user) {
    redirect("/app");
  }

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
