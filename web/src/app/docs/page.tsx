import Link from "next/link";

export const metadata = {
  title: "Documentation - SqlOS",
  description:
    "SqlOS documentation for the merged AuthServer and Fga runtime, shared example stack, and SQL-backed test setup.",
};

export default function DocsPage() {
  return (
    <div className="mx-auto max-w-5xl px-6 py-16">
      <div className="max-w-3xl">
        <div className="text-xs font-mono uppercase tracking-[0.18em] text-stone-500">
          Documentation
        </div>
        <h1 className="mt-4 text-4xl font-semibold tracking-tight text-stone-950">
          SqlOS docs
        </h1>
        <p className="mt-4 text-lg leading-8 text-stone-700">
          SqlOS is one runtime with two modules:{" "}
          <strong className="text-stone-950">AuthServer</strong> and{" "}
          <strong className="text-stone-950">Fga</strong>. These docs focus on
          how to wire it into an existing .NET app, how the example stack is
          structured, and what the library owns for you.
        </p>
      </div>

      <div className="mt-12 grid gap-6 lg:grid-cols-[0.8fr_1.2fr]">
        <aside className="h-fit rounded-2xl border border-stone-200 bg-white p-5 shadow-sm">
          <div className="text-sm font-semibold text-stone-950">On this page</div>
          <nav className="mt-4 space-y-2 text-sm">
            {[
              ["overview", "Overview"],
              ["install", "Install"],
              ["runtime-shape", "Runtime shape"],
              ["dbcontext", "DbContext setup"],
              ["startup", "Startup"],
              ["example-stack", "Example stack"],
              ["module-auth", "AuthServer module"],
              ["module-fga", "Fga module"],
              ["schema", "Schema management"],
              ["testing", "Testing"],
            ].map(([id, label]) => (
              <a
                key={id}
                href={`#${id}`}
                className="block text-stone-600 transition-colors hover:text-stone-950"
              >
                {label}
              </a>
            ))}
          </nav>
        </aside>

        <div className="space-y-14">
          <Section id="overview" title="Overview">
            <p>
              The merged runtime is designed for application teams that want auth
              and authorization embedded directly into their ASP.NET and EF Core
              stack.
            </p>
            <ul>
              <li>
                <strong>AuthServer</strong> manages organizations, users,
                credentials, sessions, refresh tokens, and SAML organization SSO.
              </li>
              <li>
                <strong>Fga</strong> manages hierarchical resources, grants,
                roles, permission keys, and query-time filtering.
              </li>
              <li>
                <strong>Shared infrastructure</strong> handles startup bootstrap,
                dashboard shell, shared example integration, and SQL-backed tests.
              </li>
            </ul>
          </Section>

          <Section id="install" title="Install">
            <p>The runtime is packaged as a single library.</p>
            <CodeBlock code={`dotnet add package SqlOS`} />
          </Section>

          <Section id="runtime-shape" title="Runtime shape">
            <p>
              Root registration is handled through <code>AddSqlOS</code>, then
              modules are enabled through <code>UseFGA</code> and{" "}
              <code>UseAuthServer</code>.
            </p>
            <CodeBlock
              code={`builder.Services.AddSqlOS<AppDbContext>(options =>
{
    options.UseFGA();
    options.UseAuthServer();
});`}
            />
          </Section>

          <Section id="dbcontext" title="DbContext setup">
            <p>
              Your app context implements both integration interfaces and lets
              the library register both module models.
            </p>
            <CodeBlock
              code={`public sealed class AppDbContext : DbContext,
    ISqlOSAuthServerDbContext,
    ISqlOSFgaDbContext
{
    public DbSet<Project> Projects => Set<Project>();

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string subjectId,
        string permissionKey)
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
          </Section>

          <Section id="startup" title="Startup">
            <p>
              The library continues to own its internal SQL schema. Startup
              bootstrap is still a single call.
            </p>
            <CodeBlock
              code={`var app = builder.Build();

await app.UseSqlOSAsync();
app.MapAuthServer("/sqlos/auth");
app.UseSqlOSDashboard("/sqlos");`}
            />
            <p>
              Default route layout:
            </p>
            <ul>
              <li>
                <code>/sqlos</code>: shared dashboard shell
              </li>
              <li>
                <code>/sqlos/admin/auth</code>: auth admin UI and APIs
              </li>
              <li>
                <code>/sqlos/admin/fga</code>: FGA admin UI
              </li>
              <li>
                <code>/sqlos/auth/*</code>: auth runtime endpoints
              </li>
            </ul>
          </Section>

          <Section id="example-stack" title="Example stack">
            <p>
              The shared example is the main integration reference. It runs:
            </p>
            <ul>
              <li>
                <code>examples/SqlOS.Example.Api</code>
              </li>
              <li>
                <code>examples/SqlOS.Example.Web</code>
              </li>
              <li>
                <code>examples/SqlOS.Example.AppHost</code>
              </li>
            </ul>
            <CodeBlock
              code={`cd examples/SqlOS.Example.Web
npm install

cd /path/to/SqlOS
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj`}
            />
            <p>Useful local URLs:</p>
            <ul>
              <li>
                <code>http://localhost:5062/sqlos/</code>
              </li>
              <li>
                <code>http://localhost:5062/sqlos/admin/auth/</code>
              </li>
              <li>
                <code>http://localhost:5062/sqlos/admin/fga/</code>
              </li>
              <li>
                <code>http://localhost:5062/swagger</code>
              </li>
              <li>
                <code>http://localhost:3001/</code>
              </li>
            </ul>
          </Section>

          <Section id="module-auth" title="AuthServer module">
            <p>
              AuthServer is aimed at application-owned identity rather than a
              separate hosted service.
            </p>
            <ul>
              <li>Organizations and memberships</li>
              <li>Users, emails, and local password credentials</li>
              <li>Sessions, rotating refresh tokens, logout, revoke</li>
              <li>SAML organization login and browser code exchange</li>
              <li>Admin UI for orgs, users, clients, sessions, and SSO config</li>
            </ul>
            <CodeBlock
              code={`builder.Services.AddSqlOS<AppDbContext>(options =>
{
    options.UseAuthServer(auth =>
    {
        auth.BasePath = "/sqlos/auth";
        auth.Issuer = "https://localhost/sqlos/auth";
    });
});`}
            />
          </Section>

          <Section id="module-fga" title="Fga module">
            <p>
              Fga keeps the relational hierarchical authorization model from the
              original project: resources form a tree, grants inherit downward,
              and authorization filters compose into EF Core queries.
            </p>
            <ul>
              <li>Resources, roles, permissions, grants, and subject types</li>
              <li>Authorization filters for list queries</li>
              <li>Point checks for mutation and detail endpoints</li>
              <li>FGA dashboard for browsing resources and grant relationships</li>
            </ul>
            <CodeBlock
              code={`var filter = await authService
    .GetAuthorizationFilterAsync<Project>(
        subjectId,
        "PROJECT_VIEW");

var projects = await dbContext.Projects
    .Where(filter)
    .OrderBy(x => x.Name)
    .ToListAsync();`}
            />
          </Section>

          <Section id="schema" title="Schema management">
            <p>
              The merge keeps the same schema-management approach as before:
            </p>
            <ul>
              <li>library-owned SQL schema</li>
              <li>embedded versioned SQL scripts</li>
              <li>idempotent startup bootstrap through <code>UseSqlOSAsync()</code></li>
              <li>
                consumer EF migrations do not own the library&apos;s internal tables
              </li>
            </ul>
          </Section>

          <Section id="testing" title="Testing">
            <p>The repo uses one shared test tree and one shared solution.</p>
            <CodeBlock code={`dotnet test SqlOS.sln`} />
            <p>
              The SQL-backed integration suites run against a real SQL Server
              container through Aspire, including both the library tests and the
              shared example integration tests.
            </p>
          </Section>

          <div className="rounded-2xl border border-stone-200 bg-white p-6 shadow-sm">
            <div className="text-sm text-stone-600">
              Looking for working integration code rather than prose?
            </div>
            <div className="mt-3 flex flex-wrap gap-3">
              <Link
                href="/"
                className="rounded-md border border-stone-300 px-4 py-2 text-sm font-medium text-stone-800 transition-colors hover:bg-stone-100"
              >
                Homepage overview
              </Link>
              <Link
                href="/blog"
                className="rounded-md border border-stone-300 px-4 py-2 text-sm font-medium text-stone-800 transition-colors hover:bg-stone-100"
              >
                Background articles
              </Link>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

function Section({
  id,
  title,
  children,
}: {
  id: string;
  title: string;
  children: React.ReactNode;
}) {
  return (
    <section id={id} className="scroll-mt-24">
      <h2 className="text-2xl font-semibold tracking-tight text-stone-950">
        {title}
      </h2>
      <div className="prose mt-4 max-w-none prose-stone prose-headings:text-stone-950 prose-a:text-emerald-800 prose-strong:text-stone-950">
        {children}
      </div>
    </section>
  );
}

function CodeBlock({ code }: { code: string }) {
  return (
    <pre className="overflow-x-auto rounded-2xl bg-stone-950 p-5 text-sm leading-7 text-stone-200">
      {code}
    </pre>
  );
}
