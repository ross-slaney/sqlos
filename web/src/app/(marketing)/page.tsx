import Link from "next/link";
import AuthPageViz from "@/components/AuthPageViz";
import FgaViz from "@/components/FgaViz";
import ArchitectureViz from "@/components/ArchitectureViz";

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
      {/* ── Hero ── */}
      <section className="px-6 pt-20 pb-28 sm:pt-28 sm:pb-36">
        <div className="mx-auto max-w-3xl text-center">
          <div className="inline-flex items-center gap-2 rounded-full border border-stone-200 bg-white/80 px-3.5 py-1.5 text-[12px] font-semibold tracking-wide text-stone-500">
            <span className="h-1.5 w-1.5 rounded-full bg-emerald-500" />
            Open source &middot; MIT &middot; .NET 9
          </div>

          <h1 className="mt-8 text-[clamp(2.4rem,5vw,4rem)] leading-[1.08] font-semibold tracking-[-0.04em] text-stone-950">
            Embedded auth and authorization for .NET
          </h1>

          <p className="mx-auto mt-6 max-w-2xl text-lg leading-relaxed text-stone-500">
            SqlOS is a .NET library that adds an OAuth 2.0 auth server, branded
            login UI, social login, SAML SSO, and fine-grained authorization to
            your app — backed by your own SQL Server, shipped as a single NuGet
            package.
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
              Read the docs
            </Link>
          </div>
        </div>
      </section>

      {/* ── AuthPage + AuthServer ── */}
      <section className="border-t border-stone-200/80 px-6 py-24">
        <div className="mx-auto max-w-5xl">
          <div className="grid items-start gap-12 lg:grid-cols-[1fr_1.15fr] lg:gap-16">
            <div>
              <p className="text-[12px] font-semibold tracking-[0.1em] uppercase text-stone-400">
                AuthServer + AuthPage
              </p>
              <h2 className="mt-3 text-3xl font-semibold tracking-[-0.03em] text-stone-950 sm:text-4xl">
                Branded login UI backed by a real OAuth server
              </h2>
              <p className="mt-5 text-base leading-7 text-stone-500">
                The library includes a hosted login, signup, and logout page you
                can brand to your app. Behind it is a standards-compliant OAuth
                2.0 authorization server with PKCE, RS256 key rotation, and
                discovery endpoints.
              </p>

              <div className="mt-8 space-y-5">
                <Detail
                  title="Home realm discovery"
                  body="When a user enters their email, the library checks if their org has SAML SSO configured and routes them automatically — no provider selection UI needed."
                />
                <Detail
                  title="Social login"
                  body="Google, Microsoft, Apple, and custom OIDC providers. The library owns the callback, exchanges the code, and maps the profile. The dashboard provides copy-ready redirect URIs and setup instructions for each provider."
                />
                <Detail
                  title="Sessions and refresh tokens"
                  body="Refresh token rotation, session revocation, configurable lifetimes, and audit events — stored in your database, visible in the embedded dashboard."
                />
              </div>
            </div>

            <div className="relative lg:mt-10">
              <AuthPageViz />
            </div>
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
                Fine-grained authorization
              </p>
              <h2 className="mt-3 text-3xl font-semibold tracking-[-0.03em] text-stone-950 sm:text-4xl">
                Flat roles break down. Resource&nbsp;hierarchies don&apos;t.
              </h2>
              <p className="mt-5 text-base leading-7 text-stone-500">
                Every multi-tenant app eventually outgrows{" "}
                <code className="text-[13px] text-stone-600 bg-stone-100 px-1 py-0.5 rounded">
                  if (user.Role == &quot;Admin&quot;)
                </code>
                . You start adding scope variants, special cases, and
                per-resource overrides — until the permission model is
                unrecognizable.
              </p>
              <p className="mt-3 text-base leading-7 text-stone-500">
                SqlOS FGA takes a different approach. Your resources form a tree
                that mirrors your actual product structure. Assign a role at any
                node and it cascades to everything underneath. No role
                explosion, no special cases — just a hierarchy that matches how
                your users already think about access.
              </p>

              <div className="mt-6 space-y-1">
                {[
                  {
                    label: "Resources",
                    desc: "Define types (org, workspace, project, document) and nest them into a tree",
                  },
                  {
                    label: "Grants",
                    desc: "Assign a role to a user at any node — permissions inherit downward automatically",
                  },
                  {
                    label: "Queries",
                    desc: "Access checks fold into your EF Core LINQ as a WHERE clause, not a service call",
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
                — a formal model of hierarchical RBAC that guarantees consistent
                per-page and point check cost performance. Read the{" "}
                <Link
                  href="/blog/developers-guide-to-hierarchical-rbac"
                  className="text-blue-600 underline decoration-blue-300 underline-offset-2 hover:decoration-blue-500"
                >
                  Developer's Guide to Hierarchical RBAC
                </Link>
                .
              </p>
            </div>
          </div>
        </div>
      </section>

      {/* ── Integration ── */}
      <section className="border-t border-stone-200/80 px-6 py-24">
        <div className="mx-auto grid max-w-4xl items-start gap-12 lg:grid-cols-2 lg:gap-16">
          <div>
            <p className="text-[12px] font-semibold tracking-[0.1em] uppercase text-stone-400">
              Integration
            </p>
            <h2 className="mt-3 text-3xl font-semibold tracking-[-0.03em] text-stone-950 sm:text-4xl">
              Simple setup
            </h2>
            <p className="mt-5 text-base leading-7 text-stone-500">
              Register in DI, bootstrap at startup, map routes. The library owns
              its SQL schema and runs embedded migrations automatically — you
              don&apos;t write migration scripts or manage auth tables.
            </p>
            <p className="mt-4 text-base leading-7 text-stone-500">
              The repo includes a full example stack (Aspire AppHost + .NET API
              + Next.js frontend) that exercises every auth flow and FGA
              pattern.{" "}
              <Link
                href="/docs/guides/getting-started"
                className="font-semibold text-blue-600 underline decoration-blue-300 underline-offset-2 hover:decoration-blue-500"
              >
                Follow the getting started guide
              </Link>
              .
            </p>

            <div className="mt-8 space-y-3">
              {[
                {
                  label: "Registration",
                  detail:
                    "One AddSqlOS call — AuthServer and FGA are opt-in modules",
                },
                {
                  label: "Schema",
                  detail:
                    "Library-owned, versioned SQL scripts run automatically at startup",
                },
                {
                  label: "Endpoints",
                  detail:
                    "MapAuthServer mounts OAuth + AuthPage routes in your pipeline",
                },
                {
                  label: "Dashboard",
                  detail:
                    "Embedded admin UI for auth, FGA, and audit — password-protectable",
                },
              ].map((item) => (
                <div
                  key={item.label}
                  className="flex items-start gap-3 rounded-lg border border-stone-100 bg-stone-50/50 px-4 py-3"
                >
                  <span className="mt-0.5 text-[12px] font-bold text-stone-950 w-24 shrink-0">
                    {item.label}
                  </span>
                  <span className="text-[13px] leading-6 text-stone-500">
                    {item.detail}
                  </span>
                </div>
              ))}
            </div>
          </div>

          <div className="lg:mt-10">
            <div className="overflow-hidden rounded-xl border border-stone-200 bg-stone-950 shadow-lg">
              <div className="flex items-center gap-1.5 border-b border-white/10 px-4 py-3">
                <span className="h-2.5 w-2.5 rounded-full bg-stone-700" />
                <span className="h-2.5 w-2.5 rounded-full bg-stone-700" />
                <span className="h-2.5 w-2.5 rounded-full bg-stone-700" />
                <span className="ml-3 text-[11px] text-stone-500">
                  Program.cs
                </span>
              </div>
              <pre className="overflow-x-auto px-5 py-5 font-mono text-[13px] leading-7 text-stone-300">
                <code>{setupCode}</code>
              </pre>
            </div>

            <div className="mt-4 grid grid-cols-3 gap-3">
              <div className="rounded-xl border border-stone-200 bg-white p-4 text-center">
                <div className="text-xl font-bold text-stone-950">.NET 9</div>
                <div className="mt-1 text-[10px] text-stone-400 uppercase tracking-wider font-medium">
                  Target
                </div>
              </div>
              <div className="rounded-xl border border-stone-200 bg-white p-4 text-center">
                <div className="text-xl font-bold text-stone-950">1</div>
                <div className="mt-1 text-[10px] text-stone-400 uppercase tracking-wider font-medium">
                  NuGet pkg
                </div>
              </div>
              <div className="rounded-xl border border-stone-200 bg-white p-4 text-center">
                <div className="text-xl font-bold text-stone-950">MIT</div>
                <div className="mt-1 text-[10px] text-stone-400 uppercase tracking-wider font-medium">
                  License
                </div>
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* ── What's included ── */}
      <section className="border-t border-stone-200/80 px-6 py-24">
        <div className="mx-auto max-w-4xl">
          <p className="text-[12px] font-semibold tracking-[0.1em] uppercase text-stone-400">
            What&apos;s included
          </p>
          <h2 className="mt-3 text-3xl font-semibold tracking-[-0.03em] text-stone-950 sm:text-4xl">
            Everything in the library
          </h2>
          <p className="mt-5 max-w-2xl text-base leading-7 text-stone-500">
            Each piece solves a specific problem you&apos;d otherwise have to
            build yourself or outsource to a separate service.
          </p>

          <div className="mt-12 space-y-px overflow-hidden rounded-xl border border-stone-200">
            {[
              {
                area: "OAuth 2.0 server",
                what: "/authorize, /token, JWKS, discovery metadata",
                why: "Standard flows for any frontend or mobile app — no custom token exchange.",
              },
              {
                area: "Branded AuthPage",
                what: "Login, signup, and logout pages with your logo and colors",
                why: "Building a secure login UI from scratch takes weeks and getting CSRF/XSS right is non-trivial.",
              },
              {
                area: "Social login",
                what: "Google, Microsoft, Apple, custom OIDC",
                why: "Each provider has quirks — the library handles callbacks, token exchange, and profile mapping.",
              },
              {
                area: "SAML SSO",
                what: "Org-scoped SAML with home-realm discovery",
                why: "Enterprise customers require it. Routes users to their org's IdP by email domain.",
              },
              {
                area: "User management",
                what: "Orgs, users, memberships, sessions, refresh tokens, audit log",
                why: "Avoids building a parallel user system next to an external provider's user model.",
              },
              {
                area: "FGA engine",
                what: "Resource types, roles, permissions, grants, EF Core query filters",
                why: "Authorization logic lives next to your data — no separate service, no eventual consistency.",
              },
              {
                area: "Admin dashboard",
                what: "Embedded UI for auth, FGA, sessions, audit, security settings",
                why: "Gives operators visibility without SSH or raw database access.",
              },
              {
                area: "Key rotation",
                what: "RS256 signing key rotation with configurable intervals and grace windows",
                why: "Manual key rotation is the thing that pages you on a Saturday.",
              },
            ].map((item, i) => (
              <div
                key={item.area}
                className={`grid gap-2 p-5 sm:grid-cols-[160px_1fr_1fr] sm:gap-6 ${
                  i % 2 === 0 ? "bg-white" : "bg-stone-50/50"
                }`}
              >
                <div className="text-[13px] font-semibold text-stone-950">
                  {item.area}
                </div>
                <div className="text-[13px] leading-6 text-stone-600">
                  {item.what}
                </div>
                <div className="text-[13px] leading-6 text-stone-400 italic">
                  {item.why}
                </div>
              </div>
            ))}
          </div>
        </div>
      </section>

      {/* ── CTA ── */}
      <section className="border-t border-stone-200/80 px-6 py-24">
        <div className="mx-auto max-w-xl text-center">
          <h2 className="text-3xl font-semibold tracking-[-0.03em] text-stone-950 sm:text-4xl">
            Try it out
          </h2>
          <p className="mt-4 text-base leading-7 text-stone-500">
            Install the package, run the example stack, or read through the
            source. The repo includes integration tests and a working
            Aspire-driven demo.
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
