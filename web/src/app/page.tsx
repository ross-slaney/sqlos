import Link from "next/link";

export default function Home() {
  return (
    <div className="bg-white text-zinc-950 dark:bg-zinc-950 dark:text-zinc-50">
      {/* ── Hero ── */}
      <section className="relative overflow-hidden border-b border-zinc-200 dark:border-zinc-800">
        <div className="absolute inset-0 dot-grid" />
        <div className="absolute inset-x-0 top-0 h-px bg-gradient-to-r from-transparent via-amber-500/40 to-transparent" />

        <div className="relative mx-auto max-w-5xl px-6 pb-20 pt-16 sm:pt-20">
          <div className="flex flex-wrap gap-2 text-xs font-mono uppercase tracking-wider text-zinc-500 dark:text-zinc-500">
            <span>FGA</span>
            <span className="text-zinc-300 dark:text-zinc-700">/</span>
            <span>Hierarchical RBAC</span>
            <span className="text-zinc-300 dark:text-zinc-700">/</span>
            <span>.NET + EF Core</span>
            <span className="text-zinc-300 dark:text-zinc-700">/</span>
            <span>SQL Server</span>
          </div>

          <h1 className="mt-6 max-w-3xl text-4xl font-semibold tracking-tight text-zinc-950 dark:text-white sm:text-5xl lg:text-6xl">
            Hierarchical RBAC for .NET and SQL Server
          </h1>

          <p className="mt-6 max-w-2xl text-lg leading-relaxed text-zinc-600 dark:text-zinc-400">
            Sqlzibar is a .NET library for fine-grained authorization. Resources
            form a tree. Grants on parent nodes cascade to descendants. Access
            checks and list filtering run inside your EF Core queries via a SQL
            Server table-valued function.
          </p>

          <div className="mt-8 flex flex-wrap items-center gap-4">
            <Link
              href="/docs"
              className="rounded-md bg-zinc-900 px-5 py-2.5 text-sm font-medium text-white transition-colors hover:bg-zinc-800 dark:bg-white dark:text-zinc-900 dark:hover:bg-zinc-200"
            >
              Documentation
            </Link>
            <a
              href="https://github.com/sqlzibar/sqlzibar"
              target="_blank"
              rel="noopener noreferrer"
              className="rounded-md border border-zinc-300 px-5 py-2.5 text-sm font-medium text-zinc-700 transition-colors hover:bg-zinc-50 dark:border-zinc-700 dark:text-zinc-300 dark:hover:bg-zinc-900"
            >
              GitHub
            </a>
            <a
              href="https://github.com/ross-slaney/sqlzibar/blob/main/paper/shrbac-compsac-2026.pdf"
              target="_blank"
              rel="noopener noreferrer"
              className="text-sm font-medium text-zinc-500 underline decoration-zinc-300 underline-offset-4 hover:text-zinc-700 hover:decoration-zinc-500 dark:text-zinc-500 dark:decoration-zinc-700 dark:hover:text-zinc-300 dark:hover:decoration-zinc-500"
            >
              Read the paper
            </a>
          </div>

          {/* Hero diagram — resource tree with grant inheritance */}
          <div className="mt-16 grid gap-10 lg:grid-cols-[1fr_1px_1fr]">
            {/* Left: tree */}
            <div>
              <div className="text-xs font-mono uppercase tracking-wider text-zinc-400 dark:text-zinc-600">
                Resource tree
              </div>
              <div className="mt-4 font-mono text-[13px] leading-[2.2] text-zinc-600 dark:text-zinc-400">
                <TreeLine depth={0} label="portal_root" dim />
                <TreeLine depth={1} label="agency:acme" highlighted grantLabel="Editor" />
                <TreeLine depth={2} label="project:website" inherited />
                <TreeLine depth={2} label="project:mobile_app" inherited />
                <TreeLine depth={3} label="doc:q1-plan" inherited last />
                <TreeLine depth={1} label="agency:globex" dim last />
              </div>
            </div>

            {/* Divider */}
            <div className="hidden lg:block bg-zinc-200 dark:bg-zinc-800" />

            {/* Right: what happened */}
            <div>
              <div className="text-xs font-mono uppercase tracking-wider text-zinc-400 dark:text-zinc-600">
                The two queries
              </div>
              <div className="mt-4 space-y-6">
                <div>
                  <div className="text-sm text-zinc-500 dark:text-zinc-500">
                    Can this subject do this?
                  </div>
                  <div className="mt-1.5 font-mono text-sm text-zinc-900 dark:text-zinc-100">
                    CheckAccessAsync
                    <span className="text-zinc-400 dark:text-zinc-600">
                      (subjectId, &quot;EDIT&quot;, resourceId)
                    </span>
                  </div>
                </div>
                <div>
                  <div className="text-sm text-zinc-500 dark:text-zinc-500">
                    What can this subject see?
                  </div>
                  <div className="mt-1.5 font-mono text-sm text-zinc-900 dark:text-zinc-100">
                    GetAuthorizationFilterAsync
                    <span className="text-zinc-400 dark:text-zinc-600">
                      &lt;Project&gt;(subjectId, &quot;VIEW&quot;)
                    </span>
                  </div>
                </div>
                <div className="border-t border-zinc-200 pt-6 dark:border-zinc-800">
                  <div className="text-sm text-zinc-500 dark:text-zinc-500">
                    Grant once, inherit everywhere
                  </div>
                  <div className="mt-2 font-mono text-[13px] text-zinc-700 dark:text-zinc-300">
                    alice &rarr; <span className="text-amber-600 dark:text-amber-400">Editor</span> @ agency:acme
                  </div>
                  <div className="mt-2 flex flex-wrap gap-1.5">
                    {["PROJECT_VIEW", "PROJECT_EDIT", "DOC_VIEW", "DOC_EDIT"].map(p => (
                      <span key={p} className="rounded bg-emerald-100 px-2 py-0.5 text-xs font-mono text-emerald-700 dark:bg-emerald-500/15 dark:text-emerald-400">
                        {p}
                      </span>
                    ))}
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* ── Model ── */}
      <section className="border-b border-zinc-200 py-20 dark:border-zinc-800">
        <div className="mx-auto max-w-5xl px-6">
          <SectionLabel>Authorization model</SectionLabel>
          <h2 className="mt-3 text-3xl font-semibold tracking-tight text-zinc-950 dark:text-white">
            Five concepts
          </h2>
          <p className="mt-3 max-w-2xl text-base text-zinc-600 dark:text-zinc-400">
            Resources form a tree. A grant links a subject to a resource with a
            role. Roles contain permissions. Grants on parent resources cascade
            to all descendants.
          </p>

          {/* Concept row */}
          <div className="mt-12 grid gap-px overflow-hidden rounded-lg border border-zinc-200 bg-zinc-200 dark:border-zinc-800 dark:bg-zinc-800 sm:grid-cols-5">
            {[
              { t: "Subject", d: "User, group, or service account." },
              { t: "Resource", d: "Node in the hierarchy." },
              { t: "Grant", d: "Subject + role + resource scope." },
              { t: "Role", d: "Named set of permissions." },
              { t: "Permission", d: "Atomic capability key." },
            ].map(c => (
              <div key={c.t} className="bg-white p-5 dark:bg-zinc-950">
                <div className="text-sm font-semibold text-zinc-950 dark:text-white">{c.t}</div>
                <div className="mt-1 text-sm text-zinc-500 dark:text-zinc-500">{c.d}</div>
              </div>
            ))}
          </div>

          {/* Inheritance example */}
          <div className="mt-12 rounded-lg border border-zinc-200 dark:border-zinc-800">
            <div className="border-b border-zinc-200 px-6 py-3 dark:border-zinc-800">
              <span className="text-xs font-mono uppercase tracking-wider text-zinc-400 dark:text-zinc-600">
                Grant inheritance
              </span>
            </div>
            <div className="grid gap-8 p-6 lg:grid-cols-2">
              <div className="font-mono text-[13px] leading-[2.4] text-zinc-500 dark:text-zinc-500">
                <div>portal_root</div>
                <div className="ml-4">
                  &boxvr;&boxh;&nbsp;
                  <span className="rounded bg-amber-100 px-1.5 py-0.5 font-semibold text-amber-800 dark:bg-amber-500/15 dark:text-amber-300">
                    agency:acme
                  </span>
                  <span className="ml-2 text-[11px] text-amber-600 dark:text-amber-500">&larr; grant</span>
                </div>
                <div className="ml-4">
                  &boxvr;&boxh;&nbsp;
                  <span className="text-zinc-400 dark:text-zinc-600">agency:globex</span>
                  <span className="ml-2 text-[11px] text-zinc-400 dark:text-zinc-700">no access</span>
                </div>
                <div className="ml-8">
                  &boxvr;&boxh;&nbsp;
                  <span className="rounded bg-emerald-100 px-1.5 py-0.5 text-emerald-700 dark:bg-emerald-500/15 dark:text-emerald-400">
                    project:website
                  </span>
                  <span className="ml-2 text-[11px] text-emerald-600 dark:text-emerald-500">inherited</span>
                </div>
                <div className="ml-8">
                  &boxvr;&boxh;&nbsp;
                  <span className="rounded bg-emerald-100 px-1.5 py-0.5 text-emerald-700 dark:bg-emerald-500/15 dark:text-emerald-400">
                    project:mobile_app
                  </span>
                  <span className="ml-2 text-[11px] text-emerald-600 dark:text-emerald-500">inherited</span>
                </div>
                <div className="ml-12">
                  &boxur;&boxh;&nbsp;
                  <span className="rounded bg-emerald-100 px-1.5 py-0.5 text-emerald-700 dark:bg-emerald-500/15 dark:text-emerald-400">
                    doc:q1-plan
                  </span>
                  <span className="ml-2 text-[11px] text-emerald-600 dark:text-emerald-500">inherited</span>
                </div>
              </div>

              <div className="space-y-4 text-sm">
                <div>
                  <div className="font-mono text-xs uppercase tracking-wider text-zinc-400 dark:text-zinc-600">
                    The grant
                  </div>
                  <div className="mt-2 font-mono text-zinc-700 dark:text-zinc-300">
                    subject: <span className="text-amber-600 dark:text-amber-400">alice</span>
                    <br />
                    role: <span className="text-amber-600 dark:text-amber-400">Editor</span>
                    <br />
                    resource: <span className="text-amber-600 dark:text-amber-400">agency:acme</span>
                  </div>
                </div>
                <div className="border-t border-zinc-200 pt-4 dark:border-zinc-800">
                  <div className="font-mono text-xs uppercase tracking-wider text-zinc-400 dark:text-zinc-600">
                    Effective on all descendants
                  </div>
                  <div className="mt-2 flex flex-wrap gap-1.5">
                    {["PROJECT_VIEW", "PROJECT_EDIT", "DOC_VIEW", "DOC_EDIT"].map(p => (
                      <span key={p} className="rounded bg-zinc-100 px-2 py-0.5 font-mono text-xs text-zinc-600 dark:bg-zinc-900 dark:text-zinc-400">
                        {p}
                      </span>
                    ))}
                  </div>
                </div>
                <div className="border-t border-zinc-200 pt-4 text-zinc-600 dark:border-zinc-800 dark:text-zinc-400">
                  The TVF walks up from any target resource to the root, matching
                  grants at each ancestor. One grant covers the entire subtree.
                </div>
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* ── API ── */}
      <section className="py-20">
        <div className="mx-auto max-w-5xl px-6">
          <SectionLabel>API</SectionLabel>
          <h2 className="mt-3 text-3xl font-semibold tracking-tight text-zinc-950 dark:text-white">
            Create resources, grant roles, check access, list results
          </h2>

          <div className="mt-12 grid gap-6 lg:grid-cols-2">
            <Code
              label="Create resources"
              code={`var orgId = dbContext.CreateResource(
    "portal_root", "Acme", "organization");

var projectId = dbContext.CreateResource(
    orgId, "Payroll API", "project");

await dbContext.SaveChangesAsync();`}
            />

            <Code
              label="Grant a role"
              code={`dbContext.Set<SqlzibarGrant>().Add(new SqlzibarGrant
{
    Id = Guid.NewGuid().ToString(),
    SubjectId = aliceSubjectId,
    ResourceId = orgId,
    RoleId = editorRoleId
});

await dbContext.SaveChangesAsync();`}
            />

            <Code
              label="Check access"
              code={`var result = await authService.CheckAccessAsync(
    subjectId, "PROJECT_EDIT", projectId);

if (!result.Allowed)
    return Results.Json(
        new { error = "Permission denied" },
        statusCode: 403);`}
            />

            <Code
              label="List what a subject can see"
              code={`var filter = await authService
    .GetAuthorizationFilterAsync<Project>(
        subjectId, "PROJECT_VIEW");

var projects = await dbContext.Projects
    .Where(filter)
    .Where(p => p.IsActive)
    .OrderBy(p => p.Name)
    .Take(25)
    .ToListAsync();`}
            />

            <Code
              label="Trace access"
              code={`var trace = await authService.TraceResourceAccessAsync(
    subjectId, projectId, "PROJECT_EDIT");

// trace.AccessGranted     — bool
// trace.PathNodes         — each ancestor checked
// trace.GrantsUsed        — which grants matched
// trace.DecisionSummary   — human-readable`}
            />

            <Code
              label="Authorized detail endpoint"
              code={`app.MapGet("/api/projects/{id}", async (
    string id, AppDbContext db,
    ISqlzibarAuthService auth, HttpContext http) =>
{
    return await auth.AuthorizedDetailAsync(
        db.Projects.Include(p => p.Agency),
        p => p.Id == id,
        http.GetSubjectId(), "PROJECT_VIEW",
        p => new { p.Id, p.Name, p.Agency.Name });
});`}
            />
          </div>
        </div>
      </section>

      {/* ── Performance ── */}
      <section className="border-y border-zinc-200 bg-zinc-950 py-20 text-zinc-100 dark:border-zinc-800">
        <div className="mx-auto max-w-5xl px-6">
          <div className="text-xs font-mono uppercase tracking-wider text-zinc-500">
            Performance
          </div>
          <h2 className="mt-3 text-3xl font-semibold tracking-tight text-white">
            Resources and grants live in your database
          </h2>
          <p className="mt-4 max-w-2xl text-base text-zinc-400">
            No hosted resource registry. No library-imposed resource cap.
            Resources, grants, roles, and permissions are ordinary rows in your
            SQL Server database. List filtering runs in the SQL query path.
          </p>

          {/* Big stats */}
          <div className="mt-14 grid gap-px overflow-hidden rounded-lg border border-white/10 bg-white/10 sm:grid-cols-2 lg:grid-cols-4">
            <Stat value="3ms" label="List page at 1.2M rows" sub="k=20, full access, median" />
            <Stat value="&lt;1ms" label="Point access check" sub="Root level, 45K resources" />
            <Stat value="O(k)" label="Page fetch scales with page size" sub="Not total row count" />
            <Stat value="0" label="Resource count limit" sub="Scale follows your DB" />
          </div>

          {/* Benchmark detail */}
          <div className="mt-10 rounded-lg border border-white/10 overflow-hidden">
            <div className="border-b border-white/10 px-6 py-3">
              <span className="text-xs font-mono uppercase tracking-wider text-zinc-500">
                Benchmarks at 1.2M entities &mdash; 10 chains, 50 regions, 5K stores, 40K depts
              </span>
            </div>
            <div className="overflow-x-auto">
              <table className="w-full text-left text-sm">
                <thead>
                  <tr className="border-b border-white/10 text-xs uppercase tracking-wider text-zinc-500">
                    <th className="px-6 py-3 font-medium">Query</th>
                    <th className="px-6 py-3 font-medium">Scope</th>
                    <th className="px-6 py-3 font-medium text-right">Median</th>
                    <th className="px-6 py-3 font-medium text-right">P95</th>
                  </tr>
                </thead>
                <tbody className="font-mono text-sm">
                  <BenchRow q="List page (k=20)" scope="1.2M rows, full access" median="3.22" p95="5.39" />
                  <BenchRow q="List page (k=20)" scope="Single store (~240)" median="1.83" p95="2.21" />
                  <BenchRow q="Point check" scope="Root (0 hops)" median="0.98" p95="1.23" />
                  <BenchRow q="Point check" scope="Depth=4 (dept)" median="1.69" p95="3.16" />
                  <BenchRow q="Cursor page 500" scope="120K accessible" median="2.74" p95="3.50" last />
                </tbody>
              </table>
            </div>
          </div>

          <div className="mt-8 grid gap-6 text-sm text-zinc-400 sm:grid-cols-3">
            <div>
              <div className="font-medium text-zinc-300">Page size is the dominant factor</div>
              <div className="mt-1">k=10: 2ms. k=20: 3ms. k=50: 6ms. k=100: 12ms. Linear.</div>
            </div>
            <div>
              <div className="font-medium text-zinc-300">Total entity count does not matter</div>
              <div className="mt-1">1K to 1.2M entities: same ~3ms at k=20.</div>
            </div>
            <div>
              <div className="font-medium text-zinc-300">Cursor depth does not matter</div>
              <div className="mt-1">Page 1 and page 500 run at the same speed.</div>
            </div>
          </div>
        </div>
      </section>

      {/* ── Approach ── */}
      <section className="border-b border-zinc-200 py-20 dark:border-zinc-800">
        <div className="mx-auto max-w-5xl px-6">
          <SectionLabel>Approach</SectionLabel>
          <h2 className="mt-3 text-3xl font-semibold tracking-tight text-zinc-950 dark:text-white">
            Relational hierarchical RBAC, not tuple graphs
          </h2>
          <p className="mt-3 max-w-2xl text-base text-zinc-600 dark:text-zinc-400">
            Sqlzibar uses a resource tree with role-based grants. It does not use
            the Zanzibar-style tuple-graph model.
          </p>

          <div className="mt-10 grid gap-px overflow-hidden rounded-lg border border-zinc-200 bg-zinc-200 dark:border-zinc-800 dark:bg-zinc-800 lg:grid-cols-2">
            <div className="bg-white p-6 dark:bg-zinc-950">
              <div className="text-sm font-semibold text-zinc-950 dark:text-white">Sqlzibar</div>
              <ul className="mt-4 space-y-2.5 text-sm text-zinc-600 dark:text-zinc-400">
                <ApproachItem accent>Resources form a tree with a single parent per node</ApproachItem>
                <ApproachItem accent>Grants are (subject, role, resource) &mdash; standard relational rows</ApproachItem>
                <ApproachItem accent>Roles map to flat permission sets</ApproachItem>
                <ApproachItem accent>Sharing is expressed by additional grants, not multiple parents</ApproachItem>
                <ApproachItem accent>List filtering happens inside the SQL query via a TVF</ApproachItem>
                <ApproachItem accent>No separate resource registry to sync</ApproachItem>
              </ul>
            </div>
            <div className="bg-white p-6 dark:bg-zinc-950">
              <div className="text-sm font-semibold text-zinc-950 dark:text-white">Tuple-graph systems</div>
              <ul className="mt-4 space-y-2.5 text-sm text-zinc-600 dark:text-zinc-400">
                <ApproachItem>Authorization is a graph of (object, relation, subject) tuples</ApproachItem>
                <ApproachItem>Every resource must be registered in the auth system</ApproachItem>
                <ApproachItem>Relationships can form arbitrary DAGs</ApproachItem>
                <ApproachItem>List filtering often requires external calls or materialization</ApproachItem>
                <ApproachItem>Mental model is relation-based, not role-based</ApproachItem>
                <ApproachItem>Hosted services may impose resource count quotas</ApproachItem>
              </ul>
            </div>
          </div>
        </div>
      </section>

      {/* ── Dashboard ── */}
      <section className="py-20">
        <div className="mx-auto max-w-5xl px-6">
          <SectionLabel>Built-in dashboard</SectionLabel>
          <h2 className="mt-3 text-3xl font-semibold tracking-tight text-zinc-950 dark:text-white">
            Browse and test at /sqlzibar
          </h2>
          <p className="mt-3 max-w-2xl text-base text-zinc-600 dark:text-zinc-400">
            Mount the embedded dashboard to browse the resource tree, inspect
            subjects and grants, manage roles and permissions, run access traces,
            and import/export the schema as YAML.
          </p>

          <div className="mt-2 font-mono text-sm text-zinc-500 dark:text-zinc-600">
            app.UseSqlzibarDashboard(&quot;/sqlzibar&quot;);
          </div>

          <div className="mt-10 grid gap-px overflow-hidden rounded-lg border border-zinc-200 bg-zinc-200 dark:border-zinc-800 dark:bg-zinc-800 sm:grid-cols-2 lg:grid-cols-4">
            {[
              { t: "Resources", d: "Lazy-loading tree view with paginated children and direct grants." },
              { t: "Subjects", d: "Users, groups, agents, service accounts with grant inspection." },
              { t: "Roles & permissions", d: "Create roles, attach permissions, expand to see mappings." },
              { t: "Access tester", d: "Enter subject, resource, permission. Get the full trace." },
            ].map(c => (
              <div key={c.t} className="bg-white p-5 dark:bg-zinc-950">
                <div className="text-sm font-semibold text-zinc-950 dark:text-white">{c.t}</div>
                <div className="mt-1 text-sm text-zinc-500 dark:text-zinc-500">{c.d}</div>
              </div>
            ))}
          </div>

          <div className="mt-4 flex flex-wrap gap-1.5">
            {["resources", "subjects", "grants", "roles", "permissions", "access tester", "schema import/export"].map(t => (
              <span key={t} className="rounded bg-zinc-100 px-2 py-0.5 text-xs font-mono text-zinc-500 dark:bg-zinc-900 dark:text-zinc-500">
                {t}
              </span>
            ))}
          </div>
        </div>
      </section>

      {/* ── Quick Start ── */}
      <section className="border-t border-zinc-200 py-20 dark:border-zinc-800">
        <div className="mx-auto max-w-5xl px-6">
          <SectionLabel>Quick start</SectionLabel>
          <h2 className="mt-3 text-3xl font-semibold tracking-tight text-zinc-950 dark:text-white">
            Three steps
          </h2>

          <div className="mt-12 space-y-10">
            <Step n={1} title="Implement ISqlzibarDbContext">
              <Code
                code={`public class AppDbContext : DbContext, ISqlzibarDbContext
{
    public IQueryable<SqlzibarAccessibleResource> IsResourceAccessible(
        string resourceId, string subjectIds, string permissionId)
        => FromExpression(() =>
            IsResourceAccessible(resourceId, subjectIds, permissionId));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplySqlzibarModel(GetType());
    }
}`}
              />
            </Step>

            <Step n={2} title="Register and initialize">
              <Code
                code={`builder.Services.AddSqlzibar<AppDbContext>(options =>
{
    options.RootResourceId = "portal_root";
    options.RootResourceName = "Portal Root";
});

var app = builder.Build();
await app.UseSqlzibarAsync();
app.UseSqlzibarDashboard("/sqlzibar");`}
              />
            </Step>

            <Step n={3} title="Check access and filter lists">
              <Code
                code={`// Point check
var access = await authService.CheckAccessAsync(
    subjectId, "PROJECT_EDIT", resourceId);

// List filter — composes into the EF Core query
var filter = await authService
    .GetAuthorizationFilterAsync<Project>(subjectId, "PROJECT_VIEW");

var projects = await dbContext.Projects
    .Where(filter)
    .OrderBy(p => p.Name)
    .ToListAsync();`}
              />
            </Step>
          </div>

          <div className="mt-12 flex flex-wrap gap-6 text-sm">
            <Link
              href="/docs"
              className="font-medium text-zinc-900 underline decoration-zinc-300 underline-offset-4 hover:decoration-zinc-500 dark:text-zinc-100 dark:decoration-zinc-700 dark:hover:decoration-zinc-500"
            >
              Full documentation
            </Link>
            <a
              href="https://github.com/sqlzibar/sqlzibar"
              target="_blank"
              rel="noopener noreferrer"
              className="font-medium text-zinc-900 underline decoration-zinc-300 underline-offset-4 hover:decoration-zinc-500 dark:text-zinc-100 dark:decoration-zinc-700 dark:hover:decoration-zinc-500"
            >
              GitHub
            </a>
          </div>
        </div>
      </section>
    </div>
  );
}

/* ── Components ── */

function SectionLabel({ children }: { children: React.ReactNode }) {
  return (
    <div className="text-xs font-mono uppercase tracking-wider text-zinc-400 dark:text-zinc-600">
      {children}
    </div>
  );
}

function TreeLine({
  depth,
  label,
  highlighted = false,
  inherited = false,
  dim = false,
  grantLabel,
  last = false,
}: {
  depth: number;
  label: string;
  highlighted?: boolean;
  inherited?: boolean;
  dim?: boolean;
  grantLabel?: string;
  last?: boolean;
}) {
  const indent = depth * 20;
  const connector = depth === 0 ? "" : last ? "\u2514\u2500 " : "\u251C\u2500 ";

  let labelClass = "text-zinc-600 dark:text-zinc-400";
  if (highlighted) labelClass = "text-amber-700 dark:text-amber-300 font-semibold";
  if (inherited) labelClass = "text-emerald-700 dark:text-emerald-400";
  if (dim) labelClass = "text-zinc-400 dark:text-zinc-600";

  return (
    <div style={{ paddingLeft: `${indent}px` }}>
      <span className="text-zinc-300 dark:text-zinc-700">{connector}</span>
      <span className={labelClass}>{label}</span>
      {grantLabel && (
        <span className="ml-2 rounded bg-amber-100 px-1.5 py-0.5 text-[11px] font-medium text-amber-700 dark:bg-amber-500/15 dark:text-amber-400">
          &larr; {grantLabel}
        </span>
      )}
      {inherited && (
        <span className="ml-2 text-[11px] text-emerald-500 dark:text-emerald-600">
          inherited
        </span>
      )}
    </div>
  );
}

function Code({ label, code }: { label?: string; code: string }) {
  return (
    <div className="overflow-hidden rounded-lg border border-zinc-200 dark:border-zinc-800">
      {label && (
        <div className="border-b border-zinc-200 bg-zinc-50 px-4 py-2.5 dark:border-zinc-800 dark:bg-zinc-900">
          <span className="text-xs font-medium text-zinc-500 dark:text-zinc-500">{label}</span>
        </div>
      )}
      <div className="bg-zinc-950 p-4">
        <pre className="overflow-x-auto text-[13px] leading-relaxed text-zinc-300">
          <code>{code}</code>
        </pre>
      </div>
    </div>
  );
}

function Step({
  n,
  title,
  children,
}: {
  n: number;
  title: string;
  children: React.ReactNode;
}) {
  return (
    <div className="grid gap-4 sm:grid-cols-[48px_1fr]">
      <div className="flex h-10 w-10 items-center justify-center rounded-md bg-zinc-100 font-mono text-sm font-semibold text-zinc-600 dark:bg-zinc-900 dark:text-zinc-400">
        {n}
      </div>
      <div>
        <h3 className="text-lg font-semibold text-zinc-950 dark:text-white">
          {title}
        </h3>
        <div className="mt-3">{children}</div>
      </div>
    </div>
  );
}

function Stat({
  value,
  label,
  sub,
}: {
  value: string;
  label: string;
  sub: string;
}) {
  return (
    <div className="bg-zinc-950 p-6">
      <div
        className="text-3xl font-semibold tracking-tight text-white"
        dangerouslySetInnerHTML={{ __html: value }}
      />
      <div className="mt-1 text-sm font-medium text-zinc-300">{label}</div>
      <div className="mt-0.5 text-xs text-zinc-500">{sub}</div>
    </div>
  );
}

function BenchRow({
  q,
  scope,
  median,
  p95,
  last = false,
}: {
  q: string;
  scope: string;
  median: string;
  p95: string;
  last?: boolean;
}) {
  return (
    <tr className={last ? "" : "border-b border-white/5"}>
      <td className="px-6 py-3 font-sans text-zinc-300">{q}</td>
      <td className="px-6 py-3 text-zinc-500">{scope}</td>
      <td className="px-6 py-3 text-right text-white">{median}ms</td>
      <td className="px-6 py-3 text-right text-zinc-500">{p95}ms</td>
    </tr>
  );
}

function ApproachItem({
  children,
  accent = false,
}: {
  children: React.ReactNode;
  accent?: boolean;
}) {
  return (
    <li className="flex gap-2.5">
      <span
        className={`mt-[7px] h-1.5 w-1.5 shrink-0 rounded-full ${
          accent ? "bg-amber-500" : "bg-zinc-300 dark:bg-zinc-700"
        }`}
      />
      <span>{children}</span>
    </li>
  );
}
