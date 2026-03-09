import Link from "next/link";

export default function Home() {
  return (
    <div className="bg-stone-50 text-stone-950">
      <section className="relative overflow-hidden border-b border-stone-200">
        <div className="absolute inset-0 bg-[radial-gradient(circle_at_top_left,_rgba(22,101,52,0.10),_transparent_45%),radial-gradient(circle_at_bottom_right,_rgba(180,83,9,0.08),_transparent_35%)]" />
        <div className="absolute inset-0 dot-grid opacity-70" />

        <div className="relative mx-auto max-w-6xl px-6 pb-20 pt-16 sm:pt-20">
          <div className="flex flex-wrap gap-2 text-xs font-mono uppercase tracking-[0.18em] text-stone-500">
            <span>Open source</span>
            <span>/</span>
            <span>.NET</span>
            <span>/</span>
            <span>EF Core</span>
            <span>/</span>
            <span>SQL Server</span>
          </div>

          <div className="mt-8 grid gap-12 lg:grid-cols-[1.3fr_0.7fr] lg:items-end">
            <div>
              <h1 className="max-w-4xl text-4xl font-semibold tracking-tight text-stone-950 sm:text-5xl lg:text-6xl">
                Embedded auth and authorization for .NET apps.
              </h1>

              <p className="mt-6 max-w-3xl text-lg leading-8 text-stone-700">
                SqlOS combines two modules in one runtime:{" "}
                <strong className="text-stone-950">AuthServer</strong> for
                organizations, users, sessions, refresh tokens, and SAML SSO,
                and <strong className="text-stone-950">Fga</strong> for
                hierarchical authorization and query-time filtering in EF Core.
              </p>

              <p className="mt-4 max-w-3xl text-base leading-7 text-stone-600">
                It is built for application teams that want the library to own
                its SQL schema, initialize itself at startup, and ship with
                working example code instead of a hosted control plane.
              </p>

              <div className="mt-8 flex flex-wrap gap-3">
                <Link
                  href="/docs"
                  className="rounded-md bg-emerald-800 px-5 py-2.5 text-sm font-medium text-white transition-colors hover:bg-emerald-700"
                >
                  Read the docs
                </Link>
                <Link
                  href="/docs#example-stack"
                  className="rounded-md border border-stone-300 bg-white px-5 py-2.5 text-sm font-medium text-stone-800 transition-colors hover:bg-stone-100"
                >
                  Run the example
                </Link>
                <Link
                  href="/blog"
                  className="rounded-md border border-transparent px-5 py-2.5 text-sm font-medium text-stone-600 transition-colors hover:bg-stone-200 hover:text-stone-900"
                >
                  Read the blog
                </Link>
              </div>
            </div>

            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-1">
              {[
                {
                  title: "One runtime",
                  body: "Register one library and enable only the modules your app needs.",
                },
                {
                  title: "Library-owned schema",
                  body: "Embedded SQL scripts and startup bootstrap manage the internal tables.",
                },
                {
                  title: "Shared example stack",
                  body: "Aspire AppHost, ASP.NET API, Next.js frontend, real SQL-backed tests.",
                },
                {
                  title: "Docs-first OSS",
                  body: "The main path is documentation, example code, and integration tests.",
                },
              ].map((item) => (
                <div
                  key={item.title}
                  className="rounded-xl border border-stone-200 bg-white/90 p-5 shadow-sm"
                >
                  <div className="text-sm font-semibold text-stone-950">
                    {item.title}
                  </div>
                  <div className="mt-2 text-sm leading-6 text-stone-600">
                    {item.body}
                  </div>
                </div>
              ))}
            </div>
          </div>
        </div>
      </section>

      <section className="border-b border-stone-200 py-20">
        <div className="mx-auto max-w-6xl px-6">
          <SectionLabel>Modules</SectionLabel>
          <h2 className="mt-3 text-3xl font-semibold tracking-tight text-stone-950">
            Two modules, one integration model
          </h2>
          <p className="mt-4 max-w-3xl text-base leading-7 text-stone-600">
            AuthServer and Fga share the same runtime, dashboard shell,
            bootstrap model, and example app. You can enable both together or
            start with one.
          </p>

          <div className="mt-10 grid gap-6 lg:grid-cols-2">
            <ModuleCard
              label="AuthServer"
              title="Identity, sessions, orgs, and SSO"
              copy="Handle local login, organization membership, refresh tokens, session revocation, and SAML organization login inside the app database."
              bullets={[
                "Organizations, users, memberships, credentials",
                "Sessions, refresh rotation, token issuance",
                "SAML org SSO and PKCE-backed browser flow",
                "Auth admin UI under /sqlos/admin/auth",
              ]}
            />
            <ModuleCard
              label="Fga"
              title="Hierarchical authorization in EF Core"
              copy="Model resources as a tree, grant roles on parent nodes, and compose authorization directly into EF queries with a SQL Server TVF."
              bullets={[
                "Resource tree with inherited grants",
                "Roles and permission keys stored in SQL",
                "Query-time filtering via authorization expressions",
                "FGA admin UI under /sqlos/admin/fga",
              ]}
            />
          </div>
        </div>
      </section>

      <section className="border-b border-stone-200 py-20">
        <div className="mx-auto max-w-6xl px-6">
          <SectionLabel>Integration</SectionLabel>
          <h2 className="mt-3 text-3xl font-semibold tracking-tight text-stone-950">
            The setup stays compact
          </h2>

          <div className="mt-10 grid gap-6 lg:grid-cols-3">
            <CodeCard
              label="Service registration"
              code={`builder.Services.AddSqlOS<AppDbContext>(options =>
{
    options.UseFGA();
    options.UseAuthServer();
});`}
            />
            <CodeCard
              label="DbContext model hooks"
              code={`public sealed class AppDbContext : DbContext,
    ISqlOSAuthServerDbContext,
    ISqlOSFgaDbContext
{
    public IQueryable<SqlOSFgaAccessibleResource>
        IsResourceAccessible(string subjectId, string permissionKey)
        => FromExpression(() =>
            IsResourceAccessible(subjectId, permissionKey));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseAuthServer();
        modelBuilder.UseFGA(GetType());
    }
}`}
            />
            <CodeCard
              label="Startup"
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
          <SectionLabel>Example stack</SectionLabel>
          <h2 className="mt-3 text-3xl font-semibold tracking-tight text-stone-950">
            One example app shows the whole flow
          </h2>
          <p className="mt-4 max-w-3xl text-base leading-7 text-stone-600">
            The shared example is the main reference implementation. It uses one
            Aspire AppHost to run SQL Server, the ASP.NET backend, and a Next.js
            frontend.
          </p>

          <div className="mt-10 grid gap-6 md:grid-cols-3">
            <ExampleCard
              title="ASP.NET API"
              body="Embeds SqlOS, mounts /sqlos dashboard routes, and exposes app-owned auth endpoints that call the library directly."
            />
            <ExampleCard
              title="Next.js web"
              body="Shows local login, SSO login, session state, token debug data, and FGA-protected app screens."
            />
            <ExampleCard
              title="Aspire AppHost"
              body="Starts the database container and both app processes, matching the real integration test environment."
            />
          </div>

          <div className="mt-10 rounded-2xl border border-stone-200 bg-stone-950 p-6 text-stone-100">
            <div className="text-xs font-mono uppercase tracking-[0.18em] text-stone-400">
              Run it locally
            </div>
            <pre className="mt-4 overflow-x-auto text-sm leading-7 text-stone-200">
{`cd examples/SqlOS.Example.Web
npm install

cd /path/to/SqlOS
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj`}
            </pre>
          </div>
        </div>
      </section>

      <section className="border-b border-stone-200 py-20">
        <div className="mx-auto max-w-6xl px-6">
          <SectionLabel>What it emphasizes</SectionLabel>
          <div className="mt-6 grid gap-6 lg:grid-cols-[0.95fr_1.05fr]">
            <div>
              <h2 className="text-3xl font-semibold tracking-tight text-stone-950">
                Straightforward OSS tradeoffs
              </h2>
              <p className="mt-4 max-w-2xl text-base leading-7 text-stone-600">
                SqlOS is not trying to be a hosted IAM platform. The project is
                opinionated toward teams that want the auth and authorization
                runtime inside their own application stack.
              </p>
            </div>

            <div className="grid gap-px overflow-hidden rounded-2xl border border-stone-200 bg-stone-200 sm:grid-cols-2">
              {[
                "Owns its schema in SQL Server instead of asking the consumer to manage internal tables.",
                "Ships real example code and real-SQL test coverage instead of only abstract API docs.",
                "Keeps auth and authorization close enough to share one example app and one dashboard shell.",
                "Optimizes for practical .NET integration rather than protocol breadth or hosted-service features.",
              ].map((item) => (
                <div key={item} className="bg-white p-5 text-sm leading-6 text-stone-700">
                  {item}
                </div>
              ))}
            </div>
          </div>
        </div>
      </section>

      <section className="py-20">
        <div className="mx-auto max-w-6xl px-6">
          <div className="rounded-2xl border border-stone-200 bg-white p-8 shadow-sm">
            <SectionLabel>Start here</SectionLabel>
            <h2 className="mt-3 text-3xl font-semibold tracking-tight text-stone-950">
              Use the docs and the example code together
            </h2>
            <p className="mt-4 max-w-3xl text-base leading-7 text-stone-600">
              The fastest way to evaluate the project is to run the shared
              example, open the dashboard, and step through the documented setup
              flows. The site is meant to support that path, not replace it with
              marketing copy.
            </p>

            <div className="mt-8 flex flex-wrap gap-3">
              <Link
                href="/docs"
                className="rounded-md bg-stone-900 px-5 py-2.5 text-sm font-medium text-white transition-colors hover:bg-stone-800"
              >
                Documentation
              </Link>
              <Link
                href="/docs#example-stack"
                className="rounded-md border border-stone-300 px-5 py-2.5 text-sm font-medium text-stone-800 transition-colors hover:bg-stone-100"
              >
                Example stack
              </Link>
              <Link
                href="/blog"
                className="rounded-md border border-stone-300 px-5 py-2.5 text-sm font-medium text-stone-800 transition-colors hover:bg-stone-100"
              >
                Background articles
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
    <div className="text-xs font-mono uppercase tracking-[0.18em] text-stone-500">
      {children}
    </div>
  );
}

function ModuleCard({
  label,
  title,
  copy,
  bullets,
}: {
  label: string;
  title: string;
  copy: string;
  bullets: string[];
}) {
  return (
    <div className="rounded-2xl border border-stone-200 bg-white p-6 shadow-sm">
      <div className="text-xs font-mono uppercase tracking-[0.18em] text-emerald-700">
        {label}
      </div>
      <h3 className="mt-3 text-xl font-semibold text-stone-950">{title}</h3>
      <p className="mt-3 text-sm leading-6 text-stone-600">{copy}</p>
      <ul className="mt-5 space-y-2 text-sm leading-6 text-stone-700">
        {bullets.map((bullet) => (
          <li key={bullet} className="flex gap-3">
            <span className="mt-2 h-1.5 w-1.5 rounded-full bg-emerald-700" />
            <span>{bullet}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

function ExampleCard({
  title,
  body,
}: {
  title: string;
  body: string;
}) {
  return (
    <div className="rounded-2xl border border-stone-200 bg-white p-6 shadow-sm">
      <div className="text-base font-semibold text-stone-950">{title}</div>
      <div className="mt-3 text-sm leading-6 text-stone-600">{body}</div>
    </div>
  );
}

function CodeCard({
  label,
  code,
}: {
  label: string;
  code: string;
}) {
  return (
    <div className="overflow-hidden rounded-2xl border border-stone-200 bg-white shadow-sm">
      <div className="border-b border-stone-200 px-5 py-3 text-xs font-mono uppercase tracking-[0.18em] text-stone-500">
        {label}
      </div>
      <pre className="overflow-x-auto bg-stone-950 p-5 text-sm leading-7 text-stone-200">
        {code}
      </pre>
    </div>
  );
}
