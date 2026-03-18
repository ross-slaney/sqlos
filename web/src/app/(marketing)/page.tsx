import Link from "next/link";
import AuthPageViz from "@/components/AuthPageViz";
import AuthStackViz from "@/components/AuthStackViz";
import FgaViz from "@/components/FgaViz";
import HeroViz from "@/components/HeroViz";

const fgaCode = `// Authorization is a WHERE clause, not a service call
var filter = await fga.BuildFilterAsync<Project>(
    subjectId: user.Id,
    permissionKey: "projects.read");

var projects = await db.Projects
    .Where(filter)          // ← TVF folds into the query plan
    .Where(p => p.IsActive)
    .OrderBy(p => p.Name)
    .Take(20)
    .ToListAsync();         // One query. One round-trip.`;

export default function Home() {
  return (
    <div>
      {/* ── Hero ── */}
      <section className="px-6 pt-16 pb-20 sm:pt-24 sm:pb-28">
        <div className="mx-auto max-w-5xl">
          <div className="grid items-center gap-12 lg:grid-cols-[1.1fr_0.9fr] lg:gap-16">
            <div>
              <h1 className="mt-6 text-[clamp(2.2rem,4.5vw,3.5rem)] leading-[1.1] font-semibold tracking-[-0.04em] text-stone-950">
                Enterprise auth for your .NET app.
              </h1>

              <p className="mt-5 max-w-lg text-base leading-7 text-stone-500">
                SqlOS gives your .NET app OAuth server, branded login, social
                auth, SAML, and fine-grained authorization — as a single NuGet
                package that runs in your process and stores in your database.
              </p>

              <div className="mt-8 flex flex-wrap items-center gap-3">
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
                  Read the docs
                </Link>
                <a
                  href="https://github.com/ross-slaney/sqlos"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="inline-flex items-center gap-1.5 px-3 py-2.5 text-[14px] font-medium text-stone-500 transition hover:text-stone-900"
                >
                  <svg
                    className="h-5 w-5"
                    fill="currentColor"
                    viewBox="0 0 24 24"
                  >
                    <path d="M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12" />
                  </svg>
                  GitHub
                </a>
              </div>
            </div>

            <HeroViz />
          </div>
        </div>
      </section>

      {/* ── AuthPage + AuthServer ── */}
      <section className="border-t border-stone-200/80 px-6 py-24">
        <div className="mx-auto max-w-5xl">
          <div className="grid items-start gap-12 lg:grid-cols-[1fr_1.15fr] lg:gap-16">
            <div>
              <p className="text-[12px] font-semibold tracking-[0.1em] uppercase text-stone-400">
                Authentication
              </p>
              <h2 className="mt-3 text-3xl font-semibold tracking-[-0.03em] text-stone-950 sm:text-4xl">
                From first user to enterprise SSO
              </h2>
              <p className="mt-5 text-base leading-7 text-stone-500">
                SqlOS ships a brandable login page backed by a real OAuth 2.0
                server — rendered from your server, not a third-party. Start
                with password auth, add social login through the dashboard, and
                enable SAML SSO when your customers need it. No code changes
                between stages.
              </p>

              <div className="mt-7 space-y-4">
                <Detail
                  title="Guided provider setup"
                  body="The dashboard walks you through Google, Microsoft, Apple, and custom OIDC configuration with provider-specific instructions and copy-ready callback URIs."
                />
                <Detail
                  title="Enterprise SSO in minutes"
                  body="Create a SAML draft, hand your customer the Entity ID and ACS URL, import their federation metadata. Home realm discovery routes users by email domain automatically."
                />
                <Detail
                  title="Sessions, keys, and audit"
                  body="Refresh token rotation, automatic RS256 key rotation with grace windows, session revocation, and a full audit log — all visible in the dashboard."
                />
              </div>
            </div>

            <div className="relative lg:mt-8">
              <AuthPageViz />
            </div>
          </div>
        </div>
      </section>

      {/* ── Auth surface — dark popout ── */}
      <section className="px-6 py-16">
        <div className="mx-auto max-w-5xl overflow-hidden rounded-2xl bg-stone-950 px-5 py-12 sm:px-12 sm:py-16">
          <div className="grid items-center gap-12 lg:grid-cols-[1.1fr_0.9fr] lg:gap-16">
            <div>
              <p className="text-[12px] font-semibold tracking-[0.1em] uppercase text-white/40">
                The auth stack
              </p>
              <h2 className="mt-3 text-3xl font-semibold tracking-[-0.03em] text-white sm:text-4xl">
                Enterprise SSO, social auth, and a whole lot more
              </h2>
              <p className="mt-5 text-base leading-7 text-white/60">
                One integration connects your app to every identity provider
                your customers use. Configure Google, Microsoft, Apple, SAML, or
                custom OIDC from the dashboard — or go headless and build your
                own login UI on top of the OAuth APIs.
              </p>
              <Link
                href="/docs/guides/getting-started"
                className="mt-6 inline-flex items-center gap-1.5 text-[14px] font-semibold text-white hover:text-white/80"
              >
                Add auth to your app
                <svg
                  className="h-4 w-4"
                  fill="none"
                  viewBox="0 0 24 24"
                  stroke="currentColor"
                  strokeWidth={2}
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    d="M9 5l7 7-7 7"
                  />
                </svg>
              </Link>
            </div>

            <AuthStackViz />
          </div>

          {/* 2×2 capability grid */}
          <div className="mt-14 grid gap-x-10 gap-y-8 sm:grid-cols-2">
            {[
              {
                title: "SSO for any provider",
                desc: "Support SAML and OIDC identity providers with a single integration. Configure per-org from the embedded dashboard.",
              },
              {
                title: "User and org management",
                desc: "Manage users, organizations, memberships, and sessions from the dashboard or programmatically via APIs.",
              },
              {
                title: "Social authentication",
                desc: "Google, Microsoft, Apple, or custom OIDC. Guided setup with provider-specific instructions and copy-ready callback URIs.",
              },
              {
                title: "Hosted UI or headless APIs",
                desc: "Use the branded AuthPage to ship fast, or build your own frontend and call the OAuth and session APIs directly.",
              },
            ].map((item) => (
              <div key={item.title}>
                <h3 className="text-[14px] font-semibold text-white">
                  {item.title}
                </h3>
                <p className="mt-1.5 text-[13px] leading-6 text-white/50">
                  {item.desc}
                </p>
              </div>
            ))}
          </div>
        </div>
      </section>

      {/* ── FGA ── */}
      <section className="border-t border-stone-200/80 px-6 py-24">
        <div className="mx-auto max-w-5xl">
          <div className="grid items-start gap-12 lg:grid-cols-[1.15fr_1fr] lg:gap-16">
            <div className="order-2 lg:order-1">
              <FgaViz />
            </div>

            <div className="order-1 lg:order-2">
              <p className="text-[12px] font-semibold tracking-[0.1em] uppercase text-stone-400">
                Authorization
              </p>
              <h2 className="mt-3 text-3xl font-semibold tracking-[-0.03em] text-stone-950 sm:text-4xl">
                Flat roles break down. Resource hierarchies don&apos;t.
              </h2>
              <p className="mt-5 text-base leading-7 text-stone-500">
                Every multi-tenant app eventually outgrows{" "}
                <code className="text-[13px] text-stone-600 bg-stone-100 px-1 py-0.5 rounded">
                  if (user.Role == &quot;Admin&quot;)
                </code>
                . SqlOS FGA lets your resources form a tree that mirrors your
                product. Assign a role at any node and it cascades down — no
                role explosion, no special cases.
              </p>

              <div className="mt-6 space-y-1">
                {[
                  {
                    label: "Resources",
                    desc: "Define types and nest them into a hierarchy that matches your product",
                  },
                  {
                    label: "Grants",
                    desc: "Assign a role at any node — permissions inherit downward automatically",
                  },
                  {
                    label: "Queries",
                    desc: "Access checks fold into EF Core LINQ as a WHERE clause, not a service call",
                  },
                ].map((item) => (
                  <div key={item.label} className="flex items-start gap-3 py-2">
                    <span className="mt-0.5 text-[12px] font-bold text-stone-950 w-20 shrink-0">
                      {item.label}
                    </span>
                    <span className="text-[13px] leading-6 text-stone-500">
                      {item.desc}
                    </span>
                  </div>
                ))}
              </div>

              <p className="mt-5 text-[13px] leading-6 text-stone-400">
                Built on{" "}
                <a
                  href="https://github.com/ross-slaney/sqlos/blob/main/paper/shrbac-compsac-2026.pdf"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="text-blue-600 underline decoration-blue-300 underline-offset-2 hover:decoration-blue-500"
                >
                  SHRBAC
                </a>{" "}
                —
                <Link
                  href="/blog/developers-guide-to-hierarchical-rbac"
                  className="text-blue-600 underline decoration-blue-300 underline-offset-2 hover:decoration-blue-500"
                >
                  Read The Developer&apos;s Guide to Hierarchical RBAC
                </Link>
                .
              </p>
            </div>
          </div>
        </div>
      </section>

      {/* ── Authorization in LINQ ── */}
      <section className="border-t border-stone-200/80 bg-stone-950 px-6 py-24 text-white">
        <div className="mx-auto max-w-5xl">
          <div className="grid items-center gap-12 lg:grid-cols-[0.85fr_1.15fr] lg:gap-16">
            <div>
              <p className="text-[12px] font-semibold tracking-[0.1em] uppercase text-white/40">
                How it works
              </p>
              <h2 className="mt-3 text-3xl font-semibold tracking-[-0.03em] sm:text-4xl">
                Authorization is a database query, not an API call
              </h2>
              <p className="mt-5 text-base leading-7 text-white/60">
                Most auth systems make you choose: fetch data then check
                permissions, or call an external API per resource. SqlOS does
                neither. The access check is a Table-Valued Function that folds
                directly into your SQL execution plan — filtering, sorting,
                pagination, and authorization in a single query.
              </p>
              <div className="mt-6 grid grid-cols-3 gap-2 sm:gap-3">
                {[
                  { value: "3.47ms", label: "per page at 1.2M rows" },
                  { value: "<1.5ms", label: "point checks, D=10" },
                  { value: "O(k·D)", label: "bounded, N-free" },
                ].map((s) => (
                  <div key={s.label} className="text-center">
                    <div className="text-[13px] sm:text-[16px] font-bold font-mono text-white">
                      {s.value}
                    </div>
                    <div className="mt-0.5 text-[9px] sm:text-[10px] text-white/40">
                      {s.label}
                    </div>
                  </div>
                ))}
              </div>
            </div>

            <div className="overflow-hidden rounded-xl border border-white/10 bg-white/5 shadow-lg">
              <div className="flex items-center gap-1.5 border-b border-white/10 px-4 py-3">
                <span className="h-2.5 w-2.5 rounded-full bg-white/20" />
                <span className="h-2.5 w-2.5 rounded-full bg-white/20" />
                <span className="h-2.5 w-2.5 rounded-full bg-white/20" />
                <span className="ml-3 text-[11px] text-white/30">
                  ProjectsEndpoint.cs
                </span>
              </div>
              <pre className="overflow-x-auto px-4 sm:px-5 py-5 font-mono text-[11px] sm:text-[13px] leading-7 text-white/70">
                <code>{fgaCode}</code>
              </pre>
            </div>
          </div>
        </div>
      </section>

      {/* ── What ships ── */}
      <section className="border-t border-stone-200/80 px-6 py-24">
        <div className="mx-auto max-w-5xl">
          <p className="text-[12px] font-semibold tracking-[0.1em] uppercase text-stone-400">
            What ships
          </p>
          <h2 className="mt-3 text-3xl font-semibold tracking-[-0.03em] text-stone-950 sm:text-4xl">
            Everything you need for OAuth, AuthN, & AuthZ in .NET
          </h2>
          <p className="mt-5 max-w-2xl text-base leading-7 text-stone-500">
            SqlOS combines authentication and authorization in one library, with
            OAuth 2.0, SAML SSO, OIDC, a branded login page, and FGA-based
            access control. It is built for large datasets with strong
            consistency and proven performance.
          </p>

          <div className="mt-10 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {[
              {
                title: "OAuth 2.0 + PKCE",
                desc: "/authorize, /token, JWKS, and discovery endpoints in your ASP.NET pipeline",
              },
              {
                title: "Branded AuthPage",
                desc: "Server-rendered login, signup, and logout — your logo, your colors, your domain",
              },
              {
                title: "Social + OIDC",
                desc: "Google, Microsoft, Apple, and custom providers with guided setup and copy-ready callbacks",
              },
              {
                title: "SAML SSO",
                desc: "Org-scoped enterprise SSO with home realm discovery by email domain",
              },
              {
                title: "FGA engine",
                desc: "Hierarchical resources, role grants, time-windowed access, and EF Core query filters",
              },
              {
                title: "Admin dashboard",
                desc: "Embedded UI for orgs, users, providers, grants, sessions, and audit — password-protectable",
              },
              {
                title: "Key rotation",
                desc: "Automatic RS256 signing key rotation with configurable intervals and grace windows",
              },
              {
                title: "Orgs and users",
                desc: "Multi-tenant user management with memberships, sessions, refresh tokens, and audit log",
              },
              {
                title: "Example stack",
                desc: "Aspire AppHost + .NET API + Next.js frontend exercising every flow — run it, fork it",
              },
            ].map((item) => (
              <div
                key={item.title}
                className="rounded-lg border border-stone-200 bg-white p-4"
              >
                <h3 className="text-[13px] font-semibold text-stone-950">
                  {item.title}
                </h3>
                <p className="mt-1 text-[12px] leading-5 text-stone-500">
                  {item.desc}
                </p>
              </div>
            ))}
          </div>

          <div className="mt-10">
            <Link
              href="/docs/guides/getting-started"
              className="inline-flex items-center gap-1.5 text-[14px] font-semibold text-blue-600 hover:text-blue-700"
            >
              Follow the getting started guide
              <svg
                className="h-4 w-4"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
                strokeWidth={2}
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M9 5l7 7-7 7"
                />
              </svg>
            </Link>
          </div>
        </div>
      </section>

      {/* ── CTA ── */}
      <section className="border-t border-stone-200/80 px-6 py-24">
        <div className="mx-auto max-w-xl text-center">
          <h2 className="text-3xl font-semibold tracking-[-0.03em] text-stone-950 sm:text-4xl">
            Get started in minutes
          </h2>
          <p className="mt-4 text-base leading-7 text-stone-500">
            Install the package. Run the example stack. Read the source.
          </p>
          <div className="mt-6 overflow-hidden rounded-lg border border-stone-200 bg-stone-950">
            <pre className="px-4 py-3 font-mono text-[13px] text-stone-300">
              <code>dotnet add package SqlOS</code>
            </pre>
          </div>
          <div className="mt-6 flex flex-wrap items-center justify-center gap-3">
            <Link
              href="/docs/guides/getting-started"
              className="rounded-lg bg-stone-950 px-5 py-2.5 text-[14px] font-semibold text-white transition hover:bg-stone-800"
            >
              Getting started guide
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
    </div>
  );
}

function Detail({ title, body }: { title: string; body: string }) {
  return (
    <div>
      <h3 className="text-[14px] font-semibold text-stone-900">{title}</h3>
      <p className="mt-1 text-[13px] leading-6 text-stone-500">{body}</p>
    </div>
  );
}
