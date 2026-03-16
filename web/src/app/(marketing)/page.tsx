import Link from "next/link";

const features = [
  {
    title: "OAuth 2.0 + PKCE",
    body: "Standards-compliant /authorize, /token, and JWKS endpoints with automatic RS256 key rotation.",
  },
  {
    title: "Branded AuthPage",
    body: "Hosted login, signup, and logout you can brand to your product. No iframes or third-party redirects.",
  },
  {
    title: "Social login",
    body: "Google, Microsoft, Apple, and custom OIDC with copy-ready callback URIs and provider-specific setup guides.",
  },
  {
    title: "Enterprise SSO",
    body: "Org-scoped SAML with home-realm discovery by email domain. Add SSO when your customers need it.",
  },
  {
    title: "Organizations & users",
    body: "Multi-tenant user management with memberships, sessions, refresh tokens, and audit events.",
  },
  {
    title: "Fine-grained authorization",
    body: "Graph-shaped resources, hierarchical roles, and grants that filter directly in EF Core LINQ queries.",
  },
];

const setupCode = `builder.Services.AddSqlOS<AppDbContext>(options =>
{
    options.UseAuthServer();
    options.UseFGA();
});

await app.UseSqlOSAsync();
app.MapAuthServer("/sqlos/auth");
app.UseSqlOSDashboard("/sqlos");`;

export default function Home() {
  return (
    <div>
      {/* Hero */}
      <section className="px-6 pt-20 pb-24 sm:pt-28 sm:pb-32">
        <div className="mx-auto max-w-3xl text-center">
          <div className="inline-flex items-center gap-2 rounded-full border border-stone-200 bg-white/80 px-3.5 py-1.5 text-[12px] font-semibold tracking-wide text-stone-500">
            <span className="h-1.5 w-1.5 rounded-full bg-emerald-500" />
            MIT Licensed
          </div>

          <h1 className="mt-8 text-[clamp(2.4rem,5vw,4rem)] leading-[1.08] font-semibold tracking-[-0.04em] text-stone-950">
            Auth and authorization that
            lives in your database
          </h1>

          <p className="mx-auto mt-6 max-w-xl text-lg leading-relaxed text-stone-500">
            One NuGet package gives your .NET app an OAuth 2.0 server, branded login UI,
            social login, SAML SSO, and fine-grained authorization — all backed by your
            own SQL Server.
          </p>

          <div className="mt-10 flex flex-wrap items-center justify-center gap-3">
            <Link
              href="/docs/guides/getting-started"
              className="rounded-lg bg-stone-950 px-5 py-2.5 text-[14px] font-semibold text-white transition hover:bg-stone-800"
            >
              Get started
            </Link>
            <Link
              href="/docs"
              className="rounded-lg border border-stone-200 bg-white px-5 py-2.5 text-[14px] font-semibold text-stone-700 transition hover:border-stone-300 hover:bg-stone-50"
            >
              Documentation
            </Link>
          </div>
        </div>
      </section>

      {/* Code */}
      <section className="px-6 pb-24">
        <div className="mx-auto grid max-w-4xl items-center gap-12 lg:grid-cols-2 lg:gap-16">
          <div>
            <p className="text-[12px] font-semibold tracking-[0.1em] uppercase text-stone-400">
              Quick start
            </p>
            <h2 className="mt-3 text-3xl font-semibold tracking-[-0.03em] text-stone-950 sm:text-4xl">
              Three calls. That&apos;s&nbsp;it.
            </h2>
            <p className="mt-4 text-base leading-7 text-stone-500">
              Register services, configure your DbContext, map the endpoints.
              SqlOS bootstraps its own schema and serves a full admin dashboard —
              no external infrastructure required.
            </p>
            <div className="mt-6 grid gap-3">
              {[
                "Register SqlOS in DI and bootstrap at startup",
                "Mount auth and dashboard routes in your ASP.NET app",
                "Turn on social login, SSO, and FGA as you need them",
              ].map((step, i) => (
                <div key={step} className="flex items-start gap-3">
                  <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-stone-950 text-[11px] font-bold text-white">
                    {i + 1}
                  </span>
                  <span className="text-sm leading-6 text-stone-600">{step}</span>
                </div>
              ))}
            </div>
          </div>
          <div className="overflow-hidden rounded-xl border border-stone-200 bg-stone-950 shadow-lg">
            <div className="flex items-center gap-1.5 border-b border-white/10 px-4 py-3">
              <span className="h-2.5 w-2.5 rounded-full bg-stone-700" />
              <span className="h-2.5 w-2.5 rounded-full bg-stone-700" />
              <span className="h-2.5 w-2.5 rounded-full bg-stone-700" />
              <span className="ml-3 text-[11px] text-stone-500">Program.cs</span>
            </div>
            <pre className="overflow-x-auto px-5 py-5 font-mono text-[13px] leading-7 text-stone-300">
              <code>{setupCode}</code>
            </pre>
          </div>
        </div>
      </section>

      {/* Features */}
      <section className="border-t border-stone-200/80 px-6 py-24">
        <div className="mx-auto max-w-4xl">
          <p className="text-[12px] font-semibold tracking-[0.1em] uppercase text-stone-400">
            Capabilities
          </p>
          <h2 className="mt-3 text-3xl font-semibold tracking-[-0.03em] text-stone-950 sm:text-4xl">
            Everything you need, nothing you&nbsp;don&apos;t
          </h2>

          <div className="mt-12 grid gap-px overflow-hidden rounded-xl border border-stone-200 bg-stone-200 sm:grid-cols-2 lg:grid-cols-3">
            {features.map((f) => (
              <div key={f.title} className="bg-white p-6">
                <h3 className="text-[15px] font-semibold text-stone-950">
                  {f.title}
                </h3>
                <p className="mt-2 text-sm leading-6 text-stone-500">{f.body}</p>
              </div>
            ))}
          </div>
        </div>
      </section>

      {/* Why SqlOS */}
      <section className="border-t border-stone-200/80 px-6 py-24">
        <div className="mx-auto max-w-4xl">
          <p className="text-[12px] font-semibold tracking-[0.1em] uppercase text-stone-400">
            Why SqlOS
          </p>
          <h2 className="mt-3 max-w-lg text-3xl font-semibold tracking-[-0.03em] text-stone-950 sm:text-4xl">
            Your data. Your infrastructure. Your&nbsp;rules.
          </h2>

          <div className="mt-12 grid gap-6 sm:grid-cols-2">
            <div className="rounded-xl border border-stone-200 bg-white p-6">
              <p className="text-[11px] font-semibold tracking-[0.1em] uppercase text-stone-400">
                External auth services
              </p>
              <ul className="mt-5 space-y-3 text-[15px] leading-7 text-stone-500">
                <li className="flex items-start gap-2.5">
                  <span className="mt-2 h-1.5 w-1.5 shrink-0 rounded-full bg-stone-300" />
                  Data on someone else&apos;s servers
                </li>
                <li className="flex items-start gap-2.5">
                  <span className="mt-2 h-1.5 w-1.5 shrink-0 rounded-full bg-stone-300" />
                  Per-MAU pricing that scales against you
                </li>
                <li className="flex items-start gap-2.5">
                  <span className="mt-2 h-1.5 w-1.5 shrink-0 rounded-full bg-stone-300" />
                  Another vendor dependency to manage
                </li>
                <li className="flex items-start gap-2.5">
                  <span className="mt-2 h-1.5 w-1.5 shrink-0 rounded-full bg-stone-300" />
                  Limited login customization
                </li>
              </ul>
            </div>

            <div className="rounded-xl border border-emerald-200 bg-emerald-50/50 p-6">
              <p className="text-[11px] font-semibold tracking-[0.1em] uppercase text-emerald-600">
                SqlOS
              </p>
              <ul className="mt-5 space-y-3 text-[15px] leading-7 text-stone-700">
                <li className="flex items-start gap-2.5">
                  <span className="mt-2 h-1.5 w-1.5 shrink-0 rounded-full bg-emerald-500" />
                  Data stays in your SQL Server
                </li>
                <li className="flex items-start gap-2.5">
                  <span className="mt-2 h-1.5 w-1.5 shrink-0 rounded-full bg-emerald-500" />
                  MIT licensed — no usage fees
                </li>
                <li className="flex items-start gap-2.5">
                  <span className="mt-2 h-1.5 w-1.5 shrink-0 rounded-full bg-emerald-500" />
                  Single NuGet package, ships with your app
                </li>
                <li className="flex items-start gap-2.5">
                  <span className="mt-2 h-1.5 w-1.5 shrink-0 rounded-full bg-emerald-500" />
                  Fully brandable AuthPage and dashboard
                </li>
              </ul>
            </div>
          </div>
        </div>
      </section>

      {/* CTA */}
      <section className="border-t border-stone-200/80 px-6 py-24">
        <div className="mx-auto max-w-xl text-center">
          <h2 className="text-3xl font-semibold tracking-[-0.03em] text-stone-950 sm:text-4xl">
            Ready to ship?
          </h2>
          <p className="mt-4 text-base leading-7 text-stone-500">
            Add SqlOS to your .NET app in minutes. Run the example stack to see
            auth flows, FGA, and the admin dashboard working together.
          </p>
          <div className="mt-8 flex flex-wrap items-center justify-center gap-3">
            <Link
              href="/docs/guides/getting-started"
              className="rounded-lg bg-stone-950 px-5 py-2.5 text-[14px] font-semibold text-white transition hover:bg-stone-800"
            >
              Get started
            </Link>
            <Link
              href="/docs/guides/reference/api-reference"
              className="rounded-lg border border-stone-200 bg-white px-5 py-2.5 text-[14px] font-semibold text-stone-700 transition hover:border-stone-300 hover:bg-stone-50"
            >
              API reference
            </Link>
          </div>
        </div>
      </section>
    </div>
  );
}
