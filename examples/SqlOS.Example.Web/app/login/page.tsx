import { getServerSession } from "next-auth";
import { redirect } from "next/navigation";
import { LoginPanel } from "@/components/login-panel";
import { authOptions } from "@/lib/auth";

export default async function LoginPage() {
  const session = await getServerSession(authOptions);
  if (session?.user && session.accessToken) {
    redirect("/app");
  }

  return (
    <main className="shell">
      <div className="stack">
        <section className="hero">
          <h1>Login</h1>
          <p>
            Local email/password remains a direct backend API login. Organization SSO uses
            home-realm discovery plus a backend-mediated PKCE exchange.
          </p>
        </section>
        <LoginPanel />
      </div>
    </main>
  );
}
