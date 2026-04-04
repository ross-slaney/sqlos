import Link from "next/link";
import AuthPageViz from "@/components/AuthPageViz";
import AuthStackViz from "@/components/AuthStackViz";
import FgaViz from "@/components/FgaViz";
import HeroViz from "@/components/HeroViz";

const authHighlights = [
  {
    title: "Guided provider setup",
    body: "The dashboard walks you through Google, Microsoft, Apple, and custom OIDC configuration with provider-specific instructions and copy-ready callback URIs.",
  },
  {
    title: "Enterprise SSO in minutes",
    body: "Create a SAML draft, hand your customer the Entity ID and ACS URL, import their federation metadata. Home realm discovery routes users by email domain automatically.",
  },
  {
    title: "Sessions, keys, and audit",
    body: "Refresh token rotation, automatic RS256 key rotation with grace windows, session revocation, and a full audit log all visible in the dashboard.",
  },
] as const;

const authStackFeatures = [
  {
    title: "SSO for any provider",
    description: "Support SAML and OIDC identity providers with a single integration. Configure per-org from the embedded dashboard.",
  },
  {
    title: "User and org management",
    description: "Manage users, organizations, memberships, and sessions from the dashboard or programmatically via APIs.",
  },
  {
    title: "Social authentication",
    description: "Google, Microsoft, Apple, or custom OIDC. Guided setup with provider-specific instructions and copy-ready callback URIs.",
  },
  {
    title: "Hosted UI or headless APIs",
    description: "Use the branded AuthPage to ship fast, or build your own frontend and call the OAuth and session APIs directly.",
  },
] as const;

const fgaConcepts = [
  {
    label: "Resources",
    description: "Define types and nest them into a hierarchy that matches your product.",
  },
  {
    label: "Grants",
    description: "Assign a role at any node and permissions inherit downward automatically.",
  },
  {
    label: "Queries",
    description: "Access checks fold into EF Core LINQ as a WHERE clause, not a service call.",
  },
] as const;

const performanceStats = [
  { value: "3.47ms", label: "per page at 1.2M rows" },
  { value: "<1.5ms", label: "point checks, D=10" },
  { value: "O(k·D)", label: "bounded, N-free" },
] as const;

const productFeatures = [
  {
    title: "OAuth 2.0 + PKCE",
    description: "/authorize, /token, JWKS, and discovery endpoints in your ASP.NET pipeline.",
  },
  {
    title: "Branded AuthPage",
    description: "Server-rendered login, signup, and logout with your logo, your colors, and your domain.",
  },
  {
    title: "Social + OIDC",
    description: "Google, Microsoft, Apple, and custom providers with guided setup and copy-ready callbacks.",
  },
  {
    title: "SAML SSO",
    description: "Org-scoped enterprise SSO with home realm discovery by email domain.",
  },
  {
    title: "FGA engine",
    description: "Hierarchical resources, role grants, time-windowed access, and EF Core query filters.",
  },
  {
    title: "Admin dashboard",
    description: "Embedded UI for orgs, users, providers, grants, sessions, and audit with optional password protection.",
  },
  {
    title: "Key rotation",
    description: "Automatic RS256 signing key rotation with configurable intervals and grace windows.",
  },
  {
    title: "Orgs and users",
    description: "Multi-tenant user management with memberships, sessions, refresh tokens, and audit log.",
  },
  {
    title: "Example stack",
    description: "Aspire AppHost plus a .NET API and Next.js frontend exercising every flow so you can run it and fork it.",
  },
] as const;

const fgaCode = `// Authorization is a WHERE clause, not a service call
var filter = await fga.BuildFilterAsync<Project>(
    subjectId: user.Id,
    permissionKey: "projects.read");

var projects = await db.Projects
    .Where(filter)          // TVF folds into the query plan
    .Where(p => p.IsActive)
    .OrderBy(p => p.Name)
    .Take(20)
    .ToListAsync();         // One query. One round-trip.`;

export default function Home() {
  return (
    <div className="relative min-h-screen">
      <section className="relative overflow-hidden px-6 pb-20 pt-24 sm:pb-28 sm:pt-32">
        <div className="absolute inset-0 -z-10 bg-gradient-to-b from-muted/40 via-background to-background" />
        <div className="mx-auto max-w-6xl">
          <div className="grid items-center gap-12 lg:grid-cols-[1.05fr_0.95fr] lg:gap-16">
            <div>
              <span className="inline-flex items-center rounded-full border bg-background/80 px-3 py-1 text-[11px] font-semibold uppercase tracking-[0.14em] text-muted-foreground backdrop-blur">
                .NET + EF Core native
              </span>
              <h1 className="mt-6 text-[clamp(2.6rem,5vw,4.4rem)] font-semibold leading-[1.02] tracking-[-0.05em] text-foreground">
                Enterprise auth for your .NET app.
              </h1>
              <p className="mt-5 max-w-xl text-base leading-7 text-muted-foreground sm:text-lg">
                SqlOS gives your .NET app OAuth server, branded login, social auth, SAML, and fine-grained authorization as a single NuGet package that runs in your process and stores in your database.
              </p>
              <div className="mt-8 flex flex-wrap items-center gap-3">
                <Link
                  href="/docs/getting-started"
                  className="inline-flex items-center gap-2 rounded-md bg-primary px-5 py-2.5 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/90"
                >
                  Get started
                  <ArrowIcon />
                </Link>
                <Link
                  href="/docs"
                  className="inline-flex items-center gap-2 rounded-md border bg-background px-5 py-2.5 text-sm font-medium text-foreground transition-colors hover:bg-accent"
                >
                  Read the docs
                </Link>
                <a
                  href="https://github.com/ross-slaney/sqlos"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="inline-flex items-center gap-2 px-2 py-2 text-sm font-medium text-muted-foreground transition-colors hover:text-foreground"
                >
                  <GitHubIcon className="h-4 w-4" />
                  GitHub
                </a>
              </div>
            </div>

            <HeroViz />
          </div>
        </div>
      </section>

      <section className="border-t px-6 py-20 sm:py-24">
        <div className="mx-auto max-w-6xl">
          <div className="grid items-start gap-12 lg:grid-cols-[1fr_1.15fr] lg:gap-16">
            <div>
              <SectionEyebrow>Authentication</SectionEyebrow>
              <h2 className="mt-3 text-3xl font-semibold tracking-[-0.04em] text-foreground sm:text-4xl">
                From first user to enterprise SSO
              </h2>
              <p className="mt-5 text-base leading-7 text-muted-foreground">
                SqlOS ships a brandable login page backed by a real OAuth 2.0 server rendered from your server, not a third-party. Start with password auth, add social login through the dashboard, and enable SAML SSO when your customers need it. No code changes between stages.
              </p>
              <div className="mt-7 space-y-5">
                {authHighlights.map((item) => (
                  <Detail key={item.title} title={item.title} body={item.body} />
                ))}
              </div>
            </div>

            <div className="relative lg:mt-8">
              <AuthPageViz />
            </div>
          </div>
        </div>
      </section>

      <section className="px-6 py-16 sm:py-20">
        <div className="mx-auto max-w-6xl overflow-hidden rounded-[2rem] border border-zinc-800 bg-zinc-950 px-6 py-12 text-zinc-50 shadow-2xl sm:px-12 sm:py-16">
          <div className="grid items-center gap-12 lg:grid-cols-[1.1fr_0.9fr] lg:gap-16">
            <div>
              <p className="text-[11px] font-semibold uppercase tracking-[0.16em] text-zinc-400">
                The auth stack
              </p>
              <h2 className="mt-3 text-3xl font-semibold tracking-[-0.04em] sm:text-4xl">
                Enterprise SSO, social auth, and a whole lot more
              </h2>
              <p className="mt-5 text-base leading-7 text-zinc-400">
                One integration connects your app to every identity provider your customers use. Configure Google, Microsoft, Apple, SAML, or custom OIDC from the dashboard or go headless and build your own login UI on top of the OAuth APIs.
              </p>
              <Link
                href="/docs/getting-started"
                className="mt-6 inline-flex items-center gap-2 text-sm font-semibold text-zinc-50 transition-colors hover:text-zinc-300"
              >
                Add auth to your app
                <ArrowIcon />
              </Link>
            </div>

            <AuthStackViz />
          </div>

          <div className="mt-14 grid gap-x-10 gap-y-8 border-t border-white/10 pt-8 sm:grid-cols-2">
            {authStackFeatures.map((item) => (
              <div key={item.title}>
                <h3 className="text-sm font-semibold text-zinc-50">{item.title}</h3>
                <p className="mt-1.5 text-sm leading-6 text-zinc-400">{item.description}</p>
              </div>
            ))}
          </div>
        </div>
      </section>

      <section className="border-t px-6 py-20 sm:py-24">
        <div className="mx-auto max-w-6xl">
          <div className="grid items-start gap-12 lg:grid-cols-[1.15fr_1fr] lg:gap-16">
            <div className="order-2 lg:order-1">
              <FgaViz />
            </div>

            <div className="order-1 lg:order-2">
              <SectionEyebrow>Authorization</SectionEyebrow>
              <h2 className="mt-3 text-3xl font-semibold tracking-[-0.04em] text-foreground sm:text-4xl">
                Flat roles break down. Resource hierarchies do not.
              </h2>
              <p className="mt-5 text-base leading-7 text-muted-foreground">
                Every multi-tenant app eventually outgrows{" "}
                <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-[13px] text-foreground">
                  if (user.Role == &quot;Admin&quot;)
                </code>
                . SqlOS FGA lets your resources form a tree that mirrors your product. Assign a role at any node and it cascades down with no role explosion and no special cases.
              </p>

              <div className="mt-6 space-y-1">
                {fgaConcepts.map((item) => (
                  <div key={item.label} className="flex items-start gap-3 py-2">
                    <span className="mt-0.5 w-20 shrink-0 text-xs font-semibold text-foreground">
                      {item.label}
                    </span>
                    <span className="text-sm leading-6 text-muted-foreground">{item.description}</span>
                  </div>
                ))}
              </div>

              <p className="mt-5 text-sm leading-6 text-muted-foreground">
                Built on{" "}
                <a
                  href="https://github.com/ross-slaney/sqlos/blob/main/paper/shrbac-compsac-2026.pdf"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="font-medium text-primary underline underline-offset-4 hover:text-primary/80"
                >
                  SHRBAC
                </a>{" "}
                and explained in{" "}
                <Link
                  href="/blog/developers-guide-to-hierarchical-rbac"
                  className="font-medium text-primary underline underline-offset-4 hover:text-primary/80"
                >
                  The Developer&apos;s Guide to Hierarchical RBAC
                </Link>
                .
              </p>
            </div>
          </div>
        </div>
      </section>

      <section className="border-t bg-zinc-950 px-6 py-20 text-zinc-50 sm:py-24">
        <div className="mx-auto max-w-6xl">
          <div className="grid items-center gap-12 lg:grid-cols-[0.85fr_1.15fr] lg:gap-16">
            <div>
              <p className="text-[11px] font-semibold uppercase tracking-[0.16em] text-zinc-400">
                How it works
              </p>
              <h2 className="mt-3 text-3xl font-semibold tracking-[-0.04em] sm:text-4xl">
                Authorization is a database query, not an API call
              </h2>
              <p className="mt-5 text-base leading-7 text-zinc-400">
                Most auth systems make you choose: fetch data then check permissions, or call an external API per resource. SqlOS does neither. The access check is a Table-Valued Function that folds directly into your SQL execution plan with filtering, sorting, pagination, and authorization in a single query.
              </p>
              <div className="mt-6 grid grid-cols-3 gap-3">
                {performanceStats.map((stat) => (
                  <div key={stat.label} className="rounded-xl border border-white/10 bg-white/5 px-3 py-4 text-center">
                    <div className="font-mono text-sm font-bold text-white sm:text-base">
                      {stat.value}
                    </div>
                    <div className="mt-1 text-[10px] uppercase tracking-[0.12em] text-zinc-400">
                      {stat.label}
                    </div>
                  </div>
                ))}
              </div>
            </div>

            <div className="overflow-hidden rounded-2xl border border-white/10 bg-white/5 shadow-lg">
              <div className="flex items-center gap-1.5 border-b border-white/10 px-4 py-3">
                <span className="h-2.5 w-2.5 rounded-full bg-white/20" />
                <span className="h-2.5 w-2.5 rounded-full bg-white/20" />
                <span className="h-2.5 w-2.5 rounded-full bg-white/20" />
                <span className="ml-3 text-[11px] text-zinc-400">ProjectsEndpoint.cs</span>
              </div>
              <pre className="overflow-x-auto px-4 py-5 font-mono text-[11px] leading-7 text-zinc-300 sm:px-5 sm:text-[13px]">
                <code>{fgaCode}</code>
              </pre>
            </div>
          </div>
        </div>
      </section>

      <section className="border-t px-6 py-20 sm:py-24">
        <div className="mx-auto max-w-6xl">
          <SectionEyebrow>What ships</SectionEyebrow>
          <h2 className="mt-3 text-3xl font-semibold tracking-[-0.04em] text-foreground sm:text-4xl">
            Everything you need for OAuth, AuthN, and AuthZ in .NET
          </h2>
          <p className="mt-5 max-w-2xl text-base leading-7 text-muted-foreground">
            SqlOS combines authentication and authorization in one library with OAuth 2.0, SAML SSO, OIDC, a branded login page, and FGA-based access control. It is built for large datasets with strong consistency and proven performance.
          </p>

          <div className="mt-10 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {productFeatures.map((feature) => (
              <FeatureCard
                key={feature.title}
                title={feature.title}
                description={feature.description}
              />
            ))}
          </div>

          <div className="mt-10">
            <Link
              href="/docs/getting-started"
              className="inline-flex items-center gap-2 text-sm font-semibold text-primary transition-colors hover:text-primary/80"
            >
              Follow the getting started guide
              <ArrowIcon />
            </Link>
          </div>
        </div>
      </section>

      <section className="border-t px-6 py-20 sm:py-24">
        <div className="mx-auto max-w-xl text-center">
          <h2 className="text-3xl font-semibold tracking-[-0.04em] text-foreground sm:text-4xl">
            Get started in minutes
          </h2>
          <p className="mt-4 text-base leading-7 text-muted-foreground">
            Install the package. Run the example stack. Read the source.
          </p>
          <div className="mt-6 overflow-hidden rounded-xl border bg-card">
            <pre className="px-4 py-3 font-mono text-[13px] text-foreground">
              <code>dotnet add package SqlOS</code>
            </pre>
          </div>
          <div className="mt-6 flex flex-wrap items-center justify-center gap-3">
            <Link
              href="/docs/getting-started"
              className="inline-flex items-center justify-center rounded-md bg-primary px-5 py-2.5 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/90"
            >
              Getting started guide
            </Link>
            <Link
              href="/docs"
              className="inline-flex items-center justify-center rounded-md border bg-background px-5 py-2.5 text-sm font-medium text-foreground transition-colors hover:bg-accent"
            >
              Documentation
            </Link>
          </div>
        </div>
      </section>
    </div>
  );
}

function SectionEyebrow({ children }: { children: string }) {
  return (
    <p className="text-[11px] font-semibold uppercase tracking-[0.16em] text-muted-foreground">
      {children}
    </p>
  );
}

function Detail({ title, body }: { title: string; body: string }) {
  return (
    <div className="rounded-xl border bg-card/60 p-4 shadow-sm">
      <h3 className="text-sm font-semibold text-foreground">{title}</h3>
      <p className="mt-1.5 text-sm leading-6 text-muted-foreground">{body}</p>
    </div>
  );
}

function FeatureCard({ title, description }: { title: string; description: string }) {
  return (
    <div className="rounded-xl border bg-card/70 p-5 shadow-sm transition-colors hover:bg-accent/40">
      <h3 className="text-sm font-semibold text-foreground">{title}</h3>
      <p className="mt-2 text-sm leading-6 text-muted-foreground">{description}</p>
    </div>
  );
}

function ArrowIcon() {
  return (
    <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
    </svg>
  );
}

function GitHubIcon({ className }: { className?: string }) {
  return (
    <svg className={className} fill="currentColor" viewBox="0 0 24 24">
      <path d="M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12" />
    </svg>
  );
}
