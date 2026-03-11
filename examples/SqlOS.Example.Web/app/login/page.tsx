import { LoginPanel } from "@/components/login-panel";

export default async function LoginPage() {
  return (
    <main className="shell">
      <div className="stack">
        <section className="hero">
          <h1>Login</h1>
          <p>
            Local email/password remains a direct backend API login. OIDC login and organization
            SSO are both backend-mediated, and org SSO wins when the email domain matches a
            configured SAML connection.
          </p>
        </section>
        <LoginPanel />
      </div>
    </main>
  );
}
