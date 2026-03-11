import Link from "next/link";

export default function Home() {
  return (
    <div className="bg-white text-stone-950">
      <section className="border-b border-stone-200">
        <div className="mx-auto max-w-6xl px-6 pb-20 pt-16 sm:pt-20">
          <div className="flex flex-wrap gap-2 text-xs font-mono uppercase tracking-[0.18em] text-stone-400">
            <span>Open source</span>
            <span>/</span>
            <span>.NET</span>
            <span>/</span>
            <span>EF Core</span>
            <span>/</span>
            <span>SQL Server</span>
          </div>

          <h1 className="mt-8 max-w-3xl text-4xl font-semibold tracking-tight text-stone-950 sm:text-5xl">
            Authentication and authorization for .NET apps.
          </h1>

          <p className="mt-6 max-w-2xl text-lg leading-8 text-stone-600">
            SqlOS ships two modules in one runtime.{" "}
            <strong className="text-stone-800">AuthServer</strong> handles
            identity, sessions, OIDC providers, and SSO.{" "}
            <strong className="text-stone-800">FGA</strong> handles
            hierarchical authorization with query-time EF Core filtering.
          </p>

          <div className="mt-8 flex flex-wrap gap-3">
            <Link
              href="/docs"
              className="rounded-lg bg-violet-600 px-5 py-2.5 text-sm font-medium text-white transition-colors hover:bg-violet-700"
            >
              Read the docs
            </Link>
            <Link
              href="/docs/guides/getting-started"
              className="rounded-lg border border-stone-300 bg-white px-5 py-2.5 text-sm font-medium text-stone-700 transition-colors hover:bg-stone-50"
            >
              Quick start
            </Link>
            <Link
              href="/docs/guides/reference/api-reference"
              className="rounded-lg px-5 py-2.5 text-sm font-medium text-stone-500 transition-colors hover:text-stone-800"
            >
              API reference
            </Link>
          </div>

          <div className="mt-14 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            {[
              {
                title: "One runtime",
                body: "Register one library. Enable only the modules your app needs.",
              },
              {
                title: "Library-owned schema",
                body: "Embedded SQL scripts bootstrap and version the internal tables.",
              },
              {
                title: "Shared example stack",
                body: "Aspire AppHost, ASP.NET API, Next.js frontend, SQL-backed tests.",
              },
              {
                title: "SDK + API reference",
                body: "Every service, method, and contract documented with code examples.",
              },
            ].map((item) => (
              <div
                key={item.title}
                className="rounded-lg border border-stone-200 p-5"
              >
                <div className="text-sm font-semibold text-stone-900">
                  {item.title}
                </div>
                <div className="mt-2 text-sm leading-6 text-stone-500">
                  {item.body}
                </div>
              </div>
            ))}
          </div>
        </div>
      </section>

      <section className="border-b border-stone-200 py-20">
        <div className="mx-auto max-w-6xl px-6">
          <SectionLabel>Modules</SectionLabel>
          <h2 className="mt-3 text-3xl font-semibold tracking-tight text-stone-950">
            Two modules, one integration model
          </h2>
          <p className="mt-4 max-w-2xl text-base leading-7 text-stone-500">
            AuthServer and FGA share the same runtime, dashboard shell,
            bootstrap model, and example app.
          </p>

          <div className="mt-10 grid gap-6 lg:grid-cols-2">
            <ModuleCard
              label="AuthServer"
              title="Identity, sessions, and SSO"
              bullets={[
                "Sign up, sign in, and password reset flows",
                "Enterprise SAML SSO routing and configuration",
                "Google, Microsoft, Apple, and custom OIDC login",
                "Session management with refresh token rotation",
                "Organization, user, and membership management",
                "Embedded admin dashboard at /sqlos/admin/auth",
              ]}
            />
            <ModuleCard
              label="FGA"
              title="Hierarchical authorization"
              bullets={[
                "Resource tree with inherited role grants",
                "Permission keys scoped to resource types",
                "Query-time filtering via EF Core expressions",
                "Point checks for mutations and detail endpoints",
                "Subject types: users, agents, service accounts, groups",
                "Admin dashboard at /sqlos/admin/fga",
              ]}
            />
          </div>
        </div>
      </section>

      <section className="border-b border-stone-200 py-20">
        <div className="mx-auto max-w-6xl px-6">
          <SectionLabel>Integration</SectionLabel>
          <h2 className="mt-3 text-3xl font-semibold tracking-tight text-stone-950">
            Three steps to integrate
          </h2>

          <div className="mt-10 grid gap-6 lg:grid-cols-3">
            <CodeCard
              step="1"
              label="Register services"
              code={`builder.Services.AddSqlOS<AppDbContext>(options =>
{
    options.UseFGA();
    options.UseAuthServer();
});`}
            />
            <CodeCard
              step="2"
              label="Configure DbContext"
              code={`public sealed class AppDbContext : DbContext,
    ISqlOSAuthServerDbContext,
    ISqlOSFgaDbContext
{
    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        modelBuilder.UseAuthServer();
        modelBuilder.UseFGA(GetType());
    }
}`}
            />
            <CodeCard
              step="3"
              label="Bootstrap at startup"
              code={`var app = builder.Build();

await app.UseSqlOSAsync();
app.MapAuthServer("/sqlos/auth");
app.UseSqlOSDashboard("/sqlos");`}
            />
          </div>
        </div>
      </section>

      <section className="border-b border-stone-200 py-20">
        <div className="mx-auto max-w-6xl px-6">
          <SectionLabel>Example</SectionLabel>
          <h2 className="mt-3 text-3xl font-semibold tracking-tight text-stone-950">
            Full-stack example included
          </h2>
          <p className="mt-4 max-w-2xl text-base leading-7 text-stone-500">
            One Aspire AppHost runs SQL Server, the ASP.NET API, and a Next.js
            frontend. Real integration tests cover every auth flow.
          </p>

          <div className="mt-10 grid gap-4 md:grid-cols-3">
            <FeatureCard
              title="ASP.NET API"
              body="Embeds SqlOS, mounts dashboard routes, and exposes auth endpoints that call the library directly."
            />
            <FeatureCard
              title="Next.js frontend"
              body="Login, OIDC login, SSO, session management, and FGA-protected application screens."
            />
            <FeatureCard
              title="Aspire AppHost"
              body="Orchestrates SQL Server, API, and web processes in a single command."
            />
          </div>

          <div className="mt-8 overflow-hidden rounded-lg border border-stone-200">
            <div className="border-b border-stone-200 bg-stone-50 px-5 py-3 text-xs font-mono text-stone-500">
              Run locally
            </div>
            <pre className="overflow-x-auto bg-stone-950 p-5 text-sm leading-7 text-stone-300">
{`cd examples/SqlOS.Example.Web && npm install
dotnet run --project examples/SqlOS.Example.AppHost`}
            </pre>
          </div>
        </div>
      </section>

      <section className="py-20">
        <div className="mx-auto max-w-6xl px-6">
          <div className="rounded-xl border border-stone-200 bg-stone-50 p-8">
            <h2 className="text-2xl font-semibold tracking-tight text-stone-950">
              Start with the docs and the example code
            </h2>
            <p className="mt-3 max-w-2xl text-base leading-7 text-stone-500">
              Run the example, open the dashboard, and step through the setup
              guides. The SDK reference covers every service method with code
              examples.
            </p>

            <div className="mt-6 flex flex-wrap gap-3">
              <Link
                href="/docs"
                className="rounded-lg bg-violet-600 px-5 py-2.5 text-sm font-medium text-white transition-colors hover:bg-violet-700"
              >
                Documentation
              </Link>
              <Link
                href="/docs/guides/reference/sdk-reference"
                className="rounded-lg border border-stone-300 bg-white px-5 py-2.5 text-sm font-medium text-stone-700 transition-colors hover:bg-stone-50"
              >
                SDK reference
              </Link>
              <Link
                href="/docs/guides/reference/api-reference"
                className="rounded-lg border border-stone-300 bg-white px-5 py-2.5 text-sm font-medium text-stone-700 transition-colors hover:bg-stone-50"
              >
                API reference
              </Link>
            </div>
          </div>
        </div>
      </section>
    </div>
  );
}

function SectionLabel({ children }: { children: React.ReactNode }) {
  return (
    <div className="text-xs font-semibold uppercase tracking-wider text-violet-600">
      {children}
    </div>
  );
}

function ModuleCard({
  label,
  title,
  bullets,
}: {
  label: string;
  title: string;
  bullets: string[];
}) {
  return (
    <div className="rounded-xl border border-stone-200 bg-white p-6">
      <div className="text-xs font-semibold uppercase tracking-wider text-violet-600">
        {label}
      </div>
      <h3 className="mt-2 text-lg font-semibold text-stone-950">{title}</h3>
      <ul className="mt-4 space-y-2.5 text-sm leading-6 text-stone-600">
        {bullets.map((bullet) => (
          <li key={bullet} className="flex gap-2.5">
            <span className="mt-2 h-1 w-1 shrink-0 rounded-full bg-violet-400" />
            <span>{bullet}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

function FeatureCard({ title, body }: { title: string; body: string }) {
  return (
    <div className="rounded-lg border border-stone-200 bg-white p-5">
      <div className="text-sm font-semibold text-stone-900">{title}</div>
      <div className="mt-2 text-sm leading-6 text-stone-500">{body}</div>
    </div>
  );
}

function CodeCard({
  step,
  label,
  code,
}: {
  step: string;
  label: string;
  code: string;
}) {
  return (
    <div className="overflow-hidden rounded-lg border border-stone-200">
      <div className="flex items-center gap-2 border-b border-stone-200 bg-stone-50 px-5 py-3">
        <span className="flex h-5 w-5 items-center justify-center rounded-full bg-violet-600 text-xs font-bold text-white">
          {step}
        </span>
        <span className="text-xs font-medium text-stone-600">{label}</span>
      </div>
      <pre className="overflow-x-auto bg-stone-950 p-5 text-sm leading-7 text-stone-300">
        {code}
      </pre>
    </div>
  );
}
