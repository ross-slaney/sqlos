import Link from "next/link";

export default function HomePage() {
  return (
    <main className="shell">
      <div className="stack">
        <section className="hero">
          <h1>SqlOS Example Web</h1>
          <p>
            The example backend embeds SqlOS and boots the demo in headless mode by default so the
            authorize popup stays fully app-owned. SqlOS still owns the OAuth protocol underneath;
            this page shows how the same auth server can power a product-grade custom shell.
          </p>
          <div className="actions">
            <Link className="button" href="/retail">
              Open Retail Demo
            </Link>
            <Link className="button secondary" href={"/auth/authorize" as any}>
              Headless authorize UI
            </Link>
            <a className="button secondary" href="http://localhost:5062/sqlos/">
              SqlOS dashboard
            </a>
          </div>
        </section>

        <div className="grid two">
          <section className="card">
            <h2>Hosted AuthPage</h2>
            <p className="muted">
              SqlOS still ships a hosted auth page for teams that want the fastest path. This demo
              app is intentionally running in headless mode so the custom authorize UI is what you
              see when you start the flow.
            </p>
            <div className="actions">
              <a
                className="button secondary"
                href="https://github.com/sqlos/sqlos/blob/main/examples/SqlOS.Example.Api/appsettings.json"
                target="_blank"
                rel="noreferrer"
              >
                See mode config
              </a>
            </div>
          </section>
          <section className="card">
            <h2>Headless Auth</h2>
            <p className="muted">
              Best when product teams want to own the authorize popup. This demo captures a custom
              referral-source field during signup and then shows the saved value inside the app.
            </p>
            <div className="actions">
              <Link className="button secondary" href={"/auth/authorize" as any}>
                Start headless sign in
              </Link>
              <Link className="button secondary" href={"/auth/authorize?view=signup" as any}>
                Start headless signup
              </Link>
            </div>
          </section>
        </div>
      </div>
    </main>
  );
}
