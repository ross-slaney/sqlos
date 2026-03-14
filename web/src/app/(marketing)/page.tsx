import Link from "next/link";

const platformPillars = [
  {
    eyebrow: "Auth Server",
    title: "Run a real auth control plane inside your app.",
    body:
      "Ship users, sessions, organizations, refresh rotation, password flows, admin operations, and an embedded dashboard without wiring five services together.",
    bullets: [
      "Users, memberships, clients, sessions, and audit events",
      "Embedded dashboard at /sqlos/admin/auth",
      "One library registration and one runtime to operate",
    ],
  },
  {
    eyebrow: "Social + SSO",
    title: "Give teams self-serve login options with product-grade guides.",
    body:
      "Configure Google, Microsoft, Apple, and custom OIDC with copy-ready callback values and provider-specific setup guides. Add SAML SSO when accounts grow up.",
    bullets: [
      "Provider-owned setup guides for Google, Microsoft, and Apple",
      "Authserver-owned callback flow like a hosted identity platform",
      "Enterprise SAML SSO for org-scoped login",
    ],
  },
  {
    eyebrow: "FGA Graph",
    title: "Model authorization the way your product actually works.",
    body:
      "Represent resources as a graph, layer RBAC on top, and grant access at nodes that naturally inherit through the tree. Then filter queries in EF Core instead of duplicating authorization logic everywhere.",
    bullets: [
      "Graph-shaped resources with inherited grants",
      "Role and permission hierarchy mapped to your domain",
      "Query-time filtering for list endpoints and screens",
    ],
  },
];

const operatorProof = [
  { label: "Social providers", value: "Google, Microsoft, Apple, custom OIDC" },
  { label: "Enterprise login", value: "SAML SSO with org-scoped routing" },
  { label: "Authorization model", value: "Graph + RBAC + grants at resource nodes" },
];

const launchSteps = [
  "Register SqlOS once in DI and bootstrap it at startup.",
  "Mount the AuthServer and dashboard routes inside your ASP.NET app.",
  "Turn on Social Login, SSO, and FGA as your product surface demands.",
];

const gettingStartedCode = `builder.Services.AddSqlOS<AppDbContext>(options =>
{
    options.UseAuthServer();
    options.UseFGA();
});

var app = builder.Build();

await app.UseSqlOSAsync();
app.MapAuthServer("/sqlos/auth");
app.UseSqlOSDashboard("/sqlos");`;

const graphCode = `await fga.GrantRoleAsync(new SqlOSGrantRoleRequest(
    subjectType: "user",
    subjectId: user.Id,
    resourceType: "workspace",
    resourceId: workspace.Id,
    roleKey: "editor"));

var visibleProjects = await db.Projects
    .Where(await fga.BuildFilterAsync<Project>(
        subjectType: "user",
        subjectId: user.Id,
        permissionKey: "projects.read"))
    .ToListAsync();`;

const providerHighlights = [
  {
    name: "Microsoft",
    note: "Register an Entra app, paste the SqlOS callback URI, copy client ID and secret, and ship sign-in with Microsoft.",
  },
  {
    name: "Google",
    note: "Use a Web application OAuth client, paste the SqlOS callback URI, and publish Google social auth without custom OIDC wiring.",
  },
  {
    name: "Apple",
    note: "Bring the Services ID, Team ID, Key ID, and private key into SqlOS with a setup flow that matches Apple’s real requirements.",
  },
];

const marketingStats = [
  { value: "1", label: "library to register for auth, social login, SSO, and FGA" },
  { value: "0", label: "external identity SaaS dependencies required to ship the core platform" },
  { value: "100%", label: "of the runtime, schema, dashboard, and flows owned in your codebase" },
];

export default function Home() {
  return (
    <div className="bg-[var(--background)] text-[var(--foreground)]">
      <section className="premium-hero premium-noise relative isolate overflow-hidden border-b border-white/10 text-white">
        <div className="hero-beam absolute inset-x-[-12%] top-[-14rem] h-[32rem] rounded-full bg-[radial-gradient(circle_at_center,rgba(64,196,255,0.34),rgba(64,196,255,0)_58%)] blur-3xl" />
        <div className="hero-beam absolute right-[-8rem] top-20 h-72 w-72 rounded-full bg-[radial-gradient(circle_at_center,rgba(255,185,106,0.26),rgba(255,185,106,0)_62%)] blur-3xl" />
        <div className="mx-auto max-w-7xl px-6 pb-24 pt-16 sm:pb-32 sm:pt-20">
          <div className="grid gap-16 lg:grid-cols-[minmax(0,1.05fr)_minmax(0,0.95fr)] lg:items-center">
            <div>
              <div className="inline-flex items-center gap-3 rounded-full border border-white/14 bg-white/6 px-4 py-2 text-[11px] font-medium uppercase tracking-[0.26em] text-white/78 backdrop-blur">
                <span>.NET library</span>
                <span className="h-1 w-1 rounded-full bg-white/35" />
                <span>Auth server</span>
                <span className="h-1 w-1 rounded-full bg-white/35" />
                <span>Social login</span>
                <span className="h-1 w-1 rounded-full bg-white/35" />
                <span>FGA graph</span>
              </div>

              <h1 className="mt-8 max-w-4xl text-5xl font-semibold tracking-[-0.05em] sm:text-6xl lg:text-7xl">
                The identity and authorization layer your .NET product should have owned from day one.
              </h1>

              <p className="mt-7 max-w-2xl text-lg leading-8 text-white/72 sm:text-xl">
                SqlOS gives you an embedded auth server, guided Google/Microsoft/Apple social login,
                enterprise SSO, and graph-shaped authorization in one runtime. It feels like a polished
                SaaS platform, but it ships inside your application and stays inside your repo.
              </p>

              <div className="mt-9 flex flex-wrap gap-3">
                <Link
                  href="/docs/guides/getting-started"
                  className="rounded-full bg-white px-6 py-3 text-sm font-semibold text-slate-950 transition-transform duration-200 hover:-translate-y-0.5"
                >
                  Get started
                </Link>
                <Link
                  href="/docs"
                  className="rounded-full border border-white/18 bg-white/7 px-6 py-3 text-sm font-semibold text-white transition-colors hover:bg-white/12"
                >
                  Explore docs
                </Link>
                <Link
                  href="/docs/guides/reference/api-reference"
                  className="rounded-full border border-transparent px-6 py-3 text-sm font-semibold text-white/66 transition-colors hover:text-white"
                >
                  API reference
                </Link>
              </div>

              <div className="mt-12 grid gap-4 sm:grid-cols-3">
                {marketingStats.map((stat) => (
                  <div key={stat.label} className="glass-panel rounded-[1.4rem] p-5">
                    <div className="text-3xl font-semibold tracking-[-0.05em] text-white">{stat.value}</div>
                    <p className="mt-2 text-sm leading-6 text-white/66">{stat.label}</p>
                  </div>
                ))}
              </div>
            </div>

            <HeroShowcase />
          </div>

          <div className="mt-16 grid gap-4 lg:grid-cols-3">
            {operatorProof.map((item) => (
              <div
                key={item.label}
                className="glass-panel rounded-[1.3rem] border border-white/10 px-5 py-5 text-sm text-white/82"
              >
                <div className="text-[11px] uppercase tracking-[0.22em] text-white/42">{item.label}</div>
                <p className="mt-3 text-base leading-7 text-white/78">{item.value}</p>
              </div>
            ))}
          </div>
        </div>
      </section>

      <section className="border-b border-stone-200/80 bg-[linear-gradient(180deg,rgba(255,255,255,0.72),rgba(255,255,255,0))] py-24">
        <div className="mx-auto max-w-7xl px-6">
          <SectionEyebrow>Platform surface</SectionEyebrow>
          <div className="mt-4 flex flex-col gap-4 lg:flex-row lg:items-end lg:justify-between">
            <div>
              <h2 className="max-w-3xl text-4xl font-semibold tracking-[-0.045em] text-slate-950 sm:text-5xl">
                Not another auth widget. A full control plane for identity and permissions.
              </h2>
              <p className="mt-5 max-w-3xl text-lg leading-8 text-slate-600">
                Most teams end up assembling identity, social auth, SSO, authorization rules, admin tooling,
                and query filtering from separate systems. SqlOS puts those surfaces back into one cohesive
                product layer your team can operate and evolve together.
              </p>
            </div>
          </div>

          <div className="mt-12 grid gap-6 lg:grid-cols-3">
            {platformPillars.map((pillar) => (
              <CapabilityCard key={pillar.title} {...pillar} />
            ))}
          </div>
        </div>
      </section>

      <section className="border-b border-stone-200/80 py-24">
        <div className="mx-auto grid max-w-7xl gap-10 px-6 lg:grid-cols-[minmax(0,1.02fr)_minmax(0,0.98fr)] lg:items-start">
          <div>
            <SectionEyebrow>Getting started</SectionEyebrow>
            <h2 className="mt-4 text-4xl font-semibold tracking-[-0.045em] text-slate-950 sm:text-5xl">
              Stand up the platform in one integration path.
            </h2>
            <p className="mt-5 max-w-2xl text-lg leading-8 text-slate-600">
              Register SqlOS, boot the schema, map the auth routes, and open the dashboard. The first-run
              experience should feel as operationally clean as Hangfire, but with a much bigger product surface.
            </p>

            <div className="mt-10 space-y-4">
              {launchSteps.map((step, index) => (
                <div key={step} className="paper-panel rounded-[1.25rem] p-5">
                  <div className="flex items-start gap-4">
                    <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-slate-950 text-sm font-semibold text-white">
                      {index + 1}
                    </div>
                    <p className="pt-1 text-base leading-7 text-slate-700">{step}</p>
                  </div>
                </div>
              ))}
            </div>
          </div>

          <div className="space-y-6">
            <CodeWindow
              title="Install and boot"
              subtitle="AuthServer + FGA in the same app"
              code={gettingStartedCode}
            />
            <div className="paper-panel rounded-[1.5rem] p-6">
              <div className="text-[11px] font-semibold uppercase tracking-[0.22em] text-slate-500">
                What ships with the runtime
              </div>
              <div className="mt-5 grid gap-4 sm:grid-cols-2">
                {[
                  "Embedded dashboard for operators",
                  "Auth endpoints mounted inside ASP.NET",
                  "SQL-backed schema bootstrap and upgrades",
                  "Example app and test stack as working reference",
                ].map((item) => (
                  <div key={item} className="rounded-2xl border border-slate-200/80 bg-white/75 px-4 py-4 text-sm leading-6 text-slate-700">
                    {item}
                  </div>
                ))}
              </div>
            </div>
          </div>
        </div>
      </section>

      <section className="border-b border-stone-200/80 py-24">
        <div className="mx-auto max-w-7xl px-6">
          <SectionEyebrow>Social auth and SSO</SectionEyebrow>
          <div className="mt-4 grid gap-8 lg:grid-cols-[minmax(0,0.92fr)_minmax(0,1.08fr)] lg:items-start">
            <div>
              <h2 className="text-4xl font-semibold tracking-[-0.045em] text-slate-950 sm:text-5xl">
                Give app teams a premium login surface without inventing one-off provider glue.
              </h2>
              <p className="mt-5 max-w-2xl text-lg leading-8 text-slate-600">
                SqlOS already knows how to guide operators through Google, Microsoft, and Apple setup,
                own the provider callback, and hand the final code back to your app. When customers need
                enterprise identity, the same runtime also gives you org-scoped SSO.
              </p>
            </div>

            <div className="grid gap-6">
              <div className="paper-panel rounded-[1.6rem] p-6">
                <div className="flex items-center justify-between gap-4">
                  <div>
                    <div className="text-[11px] font-semibold uppercase tracking-[0.24em] text-slate-500">
                      Operator guide experience
                    </div>
                    <h3 className="mt-2 text-2xl font-semibold tracking-[-0.035em] text-slate-950">
                      Setup guides with the right values, not guesswork.
                    </h3>
                  </div>
                  <div className="rounded-full border border-slate-200 bg-white px-3 py-1 text-[11px] font-semibold uppercase tracking-[0.22em] text-slate-500">
                    sqlos.dev
                  </div>
                </div>

                <div className="mt-6 grid gap-4 md:grid-cols-3">
                  {providerHighlights.map((provider) => (
                    <div key={provider.name} className="rounded-[1.3rem] border border-slate-200/80 bg-white px-4 py-4">
                      <div className="text-sm font-semibold text-slate-950">{provider.name}</div>
                      <p className="mt-3 text-sm leading-6 text-slate-600">{provider.note}</p>
                    </div>
                  ))}
                </div>

                <div className="mt-6 rounded-[1.3rem] border border-slate-200/80 bg-slate-950 p-5 text-sm text-slate-200 shadow-[0_24px_80px_rgba(8,15,23,0.24)]">
                  <div className="flex items-center justify-between gap-3 border-b border-white/10 pb-3 text-[11px] uppercase tracking-[0.22em] text-white/46">
                    <span>Provider callback</span>
                    <span>Authserver-owned</span>
                  </div>
                  <div className="mt-4 rounded-2xl border border-white/10 bg-white/5 px-4 py-4 font-mono text-xs leading-6 text-white/82">
                    https://sqlos.dev/sqlos/auth/oidc/callback
                  </div>
                  <div className="mt-4 grid gap-3 md:grid-cols-2">
                    <MiniSignal title="Social login" body="Google, Microsoft, Apple, custom OIDC" />
                    <MiniSignal title="Enterprise expansion" body="SAML SSO for organizations that need it" />
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </section>

      <section className="border-b border-stone-200/80 py-24">
        <div className="mx-auto grid max-w-7xl gap-10 px-6 lg:grid-cols-[minmax(0,1.08fr)_minmax(0,0.92fr)] lg:items-start">
          <div>
            <SectionEyebrow>Fine-grained authorization</SectionEyebrow>
            <h2 className="mt-4 text-4xl font-semibold tracking-[-0.045em] text-slate-950 sm:text-5xl">
              Build your authorization model as a graph, then keep it close to your data.
            </h2>
            <p className="mt-5 max-w-2xl text-lg leading-8 text-slate-600">
              SqlOS FGA lets you model your actual product topology: tenants, workspaces, chains, stores,
              documents, or any other hierarchy. Grants can live on nodes, inherit naturally, and still feed
              query filters and point checks from the same model.
            </p>

            <div className="mt-8 grid gap-4 sm:grid-cols-2">
              {[
                "Graph-shaped resource tree with inheritance",
                "RBAC hierarchy that stays aligned with the graph",
                "Explicit grants at any node in the resource model",
                "EF Core filters so lists only return what the subject can access",
              ].map((item) => (
                <div key={item} className="paper-panel rounded-[1.2rem] px-4 py-4 text-sm leading-6 text-slate-700">
                  {item}
                </div>
              ))}
            </div>
          </div>

          <div className="space-y-6">
            <GraphSurface />
            <CodeWindow
              title="Grant and filter"
              subtitle="Use the graph in code, not in slides"
              code={graphCode}
            />
          </div>
        </div>
      </section>

      <section className="border-b border-stone-200/80 py-24">
        <div className="mx-auto max-w-7xl px-6">
          <SectionEyebrow>Why it lands</SectionEyebrow>
          <div className="mt-4 grid gap-8 lg:grid-cols-[minmax(0,0.92fr)_minmax(0,1.08fr)] lg:items-start">
            <div>
              <h2 className="text-4xl font-semibold tracking-[-0.045em] text-slate-950 sm:text-5xl">
                Own the platform experience without fragmenting your stack.
              </h2>
              <p className="mt-5 max-w-2xl text-lg leading-8 text-slate-600">
                SqlOS is for teams that want a real auth and authorization product inside their application stack,
                not a thin SDK wrapper around infrastructure they do not control. You get operator workflows,
                guided login configuration, and a modern authorization model while keeping the implementation
                in your codebase and the runtime in your environment.
              </p>
            </div>

            <div className="grid gap-5 md:grid-cols-2">
              {[
                {
                  title: "Product-grade operator UX",
                  body: "Dashboard flows, setup guides, and runtime endpoints designed to feel like a serious B2B platform, not an internal admin page.",
                },
                {
                  title: "Codebase-native ownership",
                  body: "Your team can debug, extend, version, and test the exact auth and authorization layer your product depends on.",
                },
                {
                  title: "One story from login to permissions",
                  body: "Social auth, enterprise SSO, users, organizations, and FGA all live in the same integration and same mental model.",
                },
                {
                  title: "Reference implementation included",
                  body: "The example API, web app, AppHost, docs, and tests are part of the product story, not an afterthought.",
                },
              ].map((item) => (
                <div key={item.title} className="paper-panel rounded-[1.35rem] p-6">
                  <div className="text-lg font-semibold tracking-[-0.025em] text-slate-950">{item.title}</div>
                  <p className="mt-3 text-base leading-7 text-slate-600">{item.body}</p>
                </div>
              ))}
            </div>
          </div>
        </div>
      </section>

      <section className="py-24">
        <div className="mx-auto max-w-7xl px-6">
          <div className="relative overflow-hidden rounded-[2rem] border border-slate-200 bg-slate-950 px-8 py-10 text-white shadow-[0_40px_120px_rgba(8,15,23,0.2)] sm:px-10 sm:py-12">
            <div className="absolute inset-y-0 right-[-8rem] w-72 rounded-full bg-[radial-gradient(circle_at_center,rgba(54,213,182,0.24),rgba(54,213,182,0)_68%)] blur-3xl" />
            <div className="absolute inset-y-0 left-[-7rem] w-72 rounded-full bg-[radial-gradient(circle_at_center,rgba(255,178,87,0.22),rgba(255,178,87,0)_68%)] blur-3xl" />
            <div className="relative grid gap-8 lg:grid-cols-[minmax(0,0.9fr)_minmax(0,1.1fr)] lg:items-center">
              <div>
                <SectionEyebrow dark>Ready to ship</SectionEyebrow>
                <h2 className="mt-4 text-4xl font-semibold tracking-[-0.045em] sm:text-5xl">
                  Start with the example. Keep the platform.
                </h2>
                <p className="mt-5 max-w-2xl text-lg leading-8 text-white/70">
                  Run the example stack, open the dashboard, turn on social login, and model your first graph.
                  The fastest path to understanding SqlOS is seeing the full product surface working together.
                </p>
                <div className="mt-8 flex flex-wrap gap-3">
                  <Link
                    href="/docs/guides/getting-started"
                    className="rounded-full bg-white px-6 py-3 text-sm font-semibold text-slate-950 transition-transform duration-200 hover:-translate-y-0.5"
                  >
                    Open getting started
                  </Link>
                  <Link
                    href="/docs/guides/reference/sdk-reference"
                    className="rounded-full border border-white/18 bg-white/8 px-6 py-3 text-sm font-semibold text-white transition-colors hover:bg-white/12"
                  >
                    Read the SDK reference
                  </Link>
                </div>
              </div>

              <CodeWindow title="Run locally" subtitle="Example stack" code={`cd examples/SqlOS.Example.Web && npm install\ndotnet run --project examples/SqlOS.Example.AppHost`} dark />
            </div>
          </div>
        </div>
      </section>
    </div>
  );
}

function SectionEyebrow({
  children,
  dark = false,
}: {
  children: string;
  dark?: boolean;
}) {
  return (
    <div
      className={`text-[11px] font-semibold uppercase tracking-[0.24em] ${
        dark ? "text-white/56" : "text-slate-500"
      }`}
    >
      {children}
    </div>
  );
}

function CapabilityCard({
  eyebrow,
  title,
  body,
  bullets,
}: {
  eyebrow: string;
  title: string;
  body: string;
  bullets: string[];
}) {
  return (
    <article className="paper-panel rounded-[1.6rem] p-6">
      <div className="text-[11px] font-semibold uppercase tracking-[0.24em] text-slate-500">
        {eyebrow}
      </div>
      <h3 className="mt-3 text-2xl font-semibold tracking-[-0.035em] text-slate-950">
        {title}
      </h3>
      <p className="mt-4 text-base leading-7 text-slate-600">{body}</p>
      <div className="mt-6 space-y-3">
        {bullets.map((bullet) => (
          <div key={bullet} className="flex items-start gap-3 rounded-2xl border border-slate-200/80 bg-white/72 px-4 py-4">
            <span className="mt-1.5 h-2.5 w-2.5 rounded-full bg-gradient-to-br from-cyan-500 to-emerald-500" />
            <span className="text-sm leading-6 text-slate-700">{bullet}</span>
          </div>
        ))}
      </div>
    </article>
  );
}

function CodeWindow({
  title,
  subtitle,
  code,
  dark = false,
}: {
  title: string;
  subtitle: string;
  code: string;
  dark?: boolean;
}) {
  return (
    <div
      className={`code-window overflow-hidden rounded-[1.6rem] border ${
        dark
          ? "border-white/12 bg-[linear-gradient(180deg,rgba(12,20,28,0.98),rgba(6,11,18,0.98))]"
          : "border-slate-200 bg-[linear-gradient(180deg,#0e1720,#091018)]"
      } shadow-[0_28px_80px_rgba(8,15,23,0.16)]`}
    >
      <div className="flex items-center justify-between border-b border-white/10 px-5 py-4">
        <div className="flex items-center gap-2">
          <span className="h-2.5 w-2.5 rounded-full bg-[#ff7a59]" />
          <span className="h-2.5 w-2.5 rounded-full bg-[#ffd166]" />
          <span className="h-2.5 w-2.5 rounded-full bg-[#5adf99]" />
        </div>
        <div className="text-right">
          <div className="text-xs font-semibold uppercase tracking-[0.24em] text-white/44">{title}</div>
          <div className="mt-1 text-xs text-white/66">{subtitle}</div>
        </div>
      </div>
      <pre className="overflow-x-auto px-5 py-5 text-sm leading-7 text-[#d7e6f5]">
        <code>{code}</code>
      </pre>
    </div>
  );
}

function MiniSignal({ title, body }: { title: string; body: string }) {
  return (
    <div className="rounded-[1rem] border border-white/10 bg-white/5 px-4 py-4">
      <div className="text-[11px] font-semibold uppercase tracking-[0.22em] text-white/42">{title}</div>
      <p className="mt-2 text-sm leading-6 text-white/78">{body}</p>
    </div>
  );
}

function HeroShowcase() {
  return (
    <div className="relative">
      <div className="absolute left-8 top-10 h-40 w-40 rounded-full bg-[radial-gradient(circle_at_center,rgba(65,183,255,0.22),rgba(65,183,255,0)_68%)] blur-3xl" />
      <div className="absolute bottom-10 right-4 h-44 w-44 rounded-full bg-[radial-gradient(circle_at_center,rgba(255,184,118,0.24),rgba(255,184,118,0)_68%)] blur-3xl" />

      <div className="relative grid gap-5">
        <div className="glass-panel float-slow rounded-[1.75rem] p-5">
          <div className="flex items-center justify-between gap-3">
            <div>
              <div className="text-[11px] uppercase tracking-[0.24em] text-white/44">Embedded AuthServer</div>
              <div className="mt-2 text-xl font-semibold tracking-[-0.03em] text-white">Operational surface your product team can actually own</div>
            </div>
            <div className="rounded-full border border-emerald-400/20 bg-emerald-400/10 px-3 py-1 text-[11px] font-semibold uppercase tracking-[0.22em] text-emerald-200">
              Live
            </div>
          </div>

          <div className="mt-6 grid gap-3 md:grid-cols-[1.15fr_0.85fr]">
            <div className="rounded-[1.25rem] border border-white/10 bg-white/6 p-4">
              <div className="flex items-center justify-between text-[11px] uppercase tracking-[0.2em] text-white/44">
                <span>Dashboard cards</span>
                <span>/sqlos/admin/auth</span>
              </div>
              <div className="mt-4 grid gap-3">
                {[
                  ["Social Login", "Google, Microsoft, Apple, custom OIDC"],
                  ["SSO", "Org-scoped SAML routing and metadata"],
                  ["Sessions", "Refresh rotation and audit visibility"],
                ].map(([title, detail]) => (
                  <div key={title} className="rounded-2xl border border-white/10 bg-black/20 px-4 py-4">
                    <div className="text-sm font-semibold text-white">{title}</div>
                    <div className="mt-1 text-sm text-white/58">{detail}</div>
                  </div>
                ))}
              </div>
            </div>

            <div className="rounded-[1.25rem] border border-white/10 bg-white/6 p-4">
              <div className="text-[11px] uppercase tracking-[0.2em] text-white/44">Provider guides</div>
              <div className="mt-4 space-y-3">
                {[
                  "Microsoft Entra app registration",
                  "Google OAuth web client",
                  "Apple Services ID and key material",
                ].map((item) => (
                  <div key={item} className="rounded-2xl border border-white/10 bg-black/20 px-4 py-3 text-sm text-white/78">
                    {item}
                  </div>
                ))}
              </div>
              <div className="mt-4 rounded-2xl border border-cyan-300/15 bg-cyan-300/8 px-4 py-4 text-sm text-cyan-50">
                SqlOS owns the provider callback and returns the final code to your app.
              </div>
            </div>
          </div>
        </div>

        <div className="glass-panel float-medium rounded-[1.75rem] p-5">
          <div className="flex items-center justify-between gap-3">
            <div>
              <div className="text-[11px] uppercase tracking-[0.24em] text-white/44">Fine-grained authorization</div>
              <div className="mt-2 text-xl font-semibold tracking-[-0.03em] text-white">Resource graph, grants at nodes, EF Core filtering downstream</div>
            </div>
            <div className="rounded-full border border-white/10 bg-white/8 px-3 py-1 text-[11px] font-semibold uppercase tracking-[0.22em] text-white/62">
              Graph ready
            </div>
          </div>

          <div className="mt-6 grid gap-4 md:grid-cols-[0.9fr_1.1fr]">
            <div className="rounded-[1.25rem] border border-white/10 bg-black/20 p-4">
              <div className="text-[11px] uppercase tracking-[0.2em] text-white/44">Resource tree</div>
              <div className="mt-4 space-y-3 text-sm text-white/76">
                {[
                  "workspace / north-america",
                  "chain / flagship-retail",
                  "store / seattle-01",
                  "inventory / handhelds",
                ].map((item) => (
                  <div key={item} className="rounded-xl border border-white/10 bg-white/6 px-3 py-3">
                    {item}
                  </div>
                ))}
              </div>
            </div>
            <div className="rounded-[1.25rem] border border-white/10 bg-black/20 p-4">
              <div className="text-[11px] uppercase tracking-[0.2em] text-white/44">Grants and roles</div>
              <div className="mt-4 grid gap-3">
                {[
                  ["owner", "workspace.* + inherited admin rights"],
                  ["operator", "location.write + inventory.adjust"],
                  ["auditor", "reports.read + inventory.read"],
                ].map(([title, detail]) => (
                  <div key={title} className="rounded-xl border border-white/10 bg-white/6 px-4 py-3">
                    <div className="text-sm font-semibold text-white">{title}</div>
                    <div className="mt-1 text-sm text-white/58">{detail}</div>
                  </div>
                ))}
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

function GraphSurface() {
  return (
    <div className="paper-panel relative overflow-hidden rounded-[1.75rem] p-6">
      <div className="absolute left-10 top-10 h-36 w-36 rounded-full bg-[radial-gradient(circle_at_center,rgba(49,196,141,0.18),rgba(49,196,141,0)_70%)] blur-3xl" />
      <div className="absolute right-[-2rem] top-24 h-40 w-40 rounded-full bg-[radial-gradient(circle_at_center,rgba(71,147,255,0.2),rgba(71,147,255,0)_70%)] blur-3xl" />
      <div className="relative">
        <div className="flex items-center justify-between gap-3">
          <div>
            <div className="text-[11px] font-semibold uppercase tracking-[0.22em] text-slate-500">Authorization graph</div>
            <div className="mt-2 text-2xl font-semibold tracking-[-0.035em] text-slate-950">Permissions travel through the resource model.</div>
          </div>
          <div className="rounded-full border border-slate-200 bg-white px-3 py-1 text-[11px] font-semibold uppercase tracking-[0.22em] text-slate-500">
            EF Core aware
          </div>
        </div>

        <div className="mt-8 grid gap-6 lg:grid-cols-[0.88fr_1.12fr] lg:items-center">
          <div className="space-y-3">
            {[
              ["workspace", "global roles and team-wide grants"],
              ["chain", "regional ownership and operations policy"],
              ["store", "local managers and floor systems"],
              ["document", "record-level access and audit traces"],
            ].map(([title, detail]) => (
              <div key={title} className="rounded-[1.2rem] border border-slate-200/80 bg-white/80 px-4 py-4">
                <div className="text-sm font-semibold text-slate-950">{title}</div>
                <div className="mt-1 text-sm leading-6 text-slate-600">{detail}</div>
              </div>
            ))}
          </div>

          <div className="rounded-[1.35rem] border border-slate-200/80 bg-[radial-gradient(circle_at_top,#ffffff,rgba(244,242,236,0.92))] p-6">
            <div className="grid gap-4 md:grid-cols-2">
              <GraphNode title="workspace" tone="from-slate-950 to-slate-700" />
              <GraphNode title="role grants" tone="from-cyan-600 to-emerald-500" />
              <GraphNode title="chain" tone="from-slate-900 to-slate-700" />
              <GraphNode title="permission keys" tone="from-amber-500 to-orange-500" />
              <GraphNode title="store" tone="from-slate-900 to-slate-700" />
              <GraphNode title="EF filters" tone="from-indigo-500 to-cyan-500" />
            </div>
            <div className="mt-6 rounded-[1.1rem] border border-slate-200 bg-slate-950 px-4 py-4 text-sm text-slate-200">
              Grants can live exactly where access is decided, then flow into query filtering and point checks.
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

function GraphNode({ title, tone }: { title: string; tone: string }) {
  return (
    <div className="relative rounded-[1.1rem] border border-slate-200 bg-white px-4 py-4 shadow-[0_18px_40px_rgba(15,23,42,0.06)]">
      <div className={`h-1.5 w-full rounded-full bg-gradient-to-r ${tone}`} />
      <div className="mt-3 text-sm font-semibold text-slate-950">{title}</div>
    </div>
  );
}
