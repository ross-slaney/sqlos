import Link from "next/link";

export const metadata = {
  title: "Documentation - Sqlzibar",
  description: "Learn how to use Sqlzibar for hierarchical RBAC in your .NET applications.",
};

export default function DocsPage() {
  return (
    <div className="mx-auto max-w-4xl px-6 py-16">
      <h1 className="text-4xl font-bold text-zinc-900 dark:text-white">
        Documentation
      </h1>
      <p className="mt-4 text-lg text-zinc-600 dark:text-zinc-400">
        Learn how to integrate Sqlzibar into your .NET application.
      </p>

      <div className="mt-12 prose prose-zinc dark:prose-invert max-w-none">
        <h2>Installation</h2>
        <p>Install Sqlzibar via NuGet:</p>
        <pre className="bg-zinc-900 text-zinc-300 rounded-lg p-4">
          <code>dotnet add package Sqlzibar</code>
        </pre>

        <h2>DbContext Setup</h2>
        <p>Implement <code>ISqlzibarDbContext</code> on your existing DbContext. You only need one method — no DbSet properties required:</p>
        <pre className="bg-zinc-900 text-zinc-300 rounded-lg p-4 overflow-x-auto">
          <code>{`public class AppDbContext : DbContext, ISqlzibarDbContext
{
    public DbSet<Project> Projects => Set<Project>();

    public IQueryable<SqlzibarAccessibleResource> IsResourceAccessible(
        string resourceId, string subjectIds, string permissionId)
        => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplySqlzibarModel(GetType());
    }
}`}</code>
        </pre>

        <h2>Service Registration</h2>
        <p>Register Sqlzibar services and initialize on startup:</p>
        <pre className="bg-zinc-900 text-zinc-300 rounded-lg p-4 overflow-x-auto">
          <code>{`builder.Services.AddSqlzibar<AppDbContext>(options =>
{
    options.Schema = "dbo";
    options.RootResourceId = "portal_root";
    options.RootResourceName = "Portal Root";
});

var app = builder.Build();

await app.UseSqlzibarAsync();            // Initialize TVF + seed core data
app.UseSqlzibarDashboard("/sqlzibar");   // Optional: mount the dashboard`}</code>
        </pre>

        <h2>Core Concepts</h2>
        
        <h3>Subjects</h3>
        <p>
          Subjects are the entities that can be granted permissions. Sqlzibar supports
          multiple subject types:
        </p>
        <ul>
          <li><strong>Users</strong> - Human users of your application</li>
          <li><strong>Agents</strong> - AI agents or bots</li>
          <li><strong>Service Accounts</strong> - Machine-to-machine identities</li>
          <li><strong>User Groups</strong> - Collections of users that share permissions</li>
        </ul>

        <h3>Resources</h3>
        <p>
          Resources form a hierarchy. When you grant a permission on a parent resource,
          it automatically applies to all child resources. For example:
        </p>
        <pre className="bg-zinc-900 text-zinc-300 rounded-lg p-4 overflow-x-auto">
          <code>{`Organization (root)
├── Team A
│   ├── Project 1
│   └── Project 2
└── Team B
    └── Project 3`}</code>
        </pre>
        <p>
          Granting &quot;read&quot; permission on &quot;Organization&quot; gives access to all teams and projects.
        </p>

        <h3>Roles and Permissions</h3>
        <p>
          Roles are collections of permissions. You grant roles to subjects on specific
          resources:
        </p>
        <pre className="bg-zinc-900 text-zinc-300 rounded-lg p-4 overflow-x-auto">
          <code>{`// Grant the "editor" role to user_123 on project_456
await authService.GrantRoleAsync(
    subjectId: "user_123",
    roleId: "editor",
    resourceId: "project_456"
);`}</code>
        </pre>

        <h2>Checking Access</h2>
        <p>Use the authorization service to check if a subject has access:</p>
        <pre className="bg-zinc-900 text-zinc-300 rounded-lg p-4 overflow-x-auto">
          <code>{`var access = await authService.CheckAccessAsync(
    subjectId, "PROJECT_EDIT", resourceId);

if (!access.Allowed)
    return Results.Json(new { error = "Permission denied" }, statusCode: 403);`}</code>
        </pre>

        <h2>Authorized Queries</h2>
        <p>
          Use the specification executor for paginated, authorized list queries:
        </p>
        <pre className="bg-zinc-900 text-zinc-300 rounded-lg p-4 overflow-x-auto">
          <code>{`var spec = PagedSpec.For<Project>(p => p.Id)
    .RequirePermission("PROJECT_VIEW")
    .SortByString("name", p => p.Name, isDefault: true)
    .Search(search, p => p.Name, p => p.Description)
    .Build(pageSize, cursor, sortBy, sortDir);

var result = await executor.ExecuteAsync(
    context.Projects, spec, subjectId,
    p => new { p.Id, p.Name, p.Status });`}</code>
        </pre>
        <p>
          For single-item lookups with automatic 404/403 handling:
        </p>
        <pre className="bg-zinc-900 text-zinc-300 rounded-lg p-4 overflow-x-auto">
          <code>{`return await authService.AuthorizedDetailAsync(
    context.Projects.Include(p => p.Agency),
    p => p.Id == id,
    subjectId, "PROJECT_VIEW",
    p => new { p.Id, p.Name, Agency = p.Agency.Name });`}</code>
        </pre>

        <h2>Creating Resources</h2>
        <p>
          Use <code>CreateResource</code> to add authorization resources when creating entities:
        </p>
        <pre className="bg-zinc-900 text-zinc-300 rounded-lg p-4 overflow-x-auto">
          <code>{`var resourceId = context.CreateResource(
    parentResourceId, request.Name, "project");

var project = new Project
{
    ResourceId = resourceId,
    Name = request.Name
};
context.Projects.Add(project);
await context.SaveChangesAsync();`}</code>
        </pre>

        <h2>Dashboard</h2>
        <p>
          Sqlzibar includes a built-in admin dashboard. Enable it by mounting the
          dashboard middleware:
        </p>
        <pre className="bg-zinc-900 text-zinc-300 rounded-lg p-4 overflow-x-auto">
          <code>{`app.UseSqlzibarDashboard("/sqlzibar");`}</code>
        </pre>
        <p>
          The dashboard provides a UI for browsing resources, subjects, grants, roles,
          permissions, and testing access.
        </p>

        <h2>YAML Schema</h2>
        <p>
          Define your authorization schema in YAML for version control and easy
          deployment:
        </p>
        <pre className="bg-zinc-900 text-zinc-300 rounded-lg p-4 overflow-x-auto">
          <code>{`resourceTypes:
  - id: organization
    name: Organization
  - id: team
    name: Team
  - id: project
    name: Project

permissions:
  - key: read
    name: Read
  - key: write
    name: Write
  - key: delete
    name: Delete

roles:
  - key: viewer
    name: Viewer
    permissions: [read]
  - key: editor
    name: Editor
    permissions: [read, write]
  - key: admin
    name: Admin
    permissions: [read, write, delete]`}</code>
        </pre>
      </div>

      <div className="mt-12 border-t border-zinc-200 pt-8 dark:border-zinc-800">
        <Link
          href="/blog"
          className="text-indigo-600 hover:text-indigo-500 font-medium"
        >
          Read our blog for in-depth guides &rarr;
        </Link>
      </div>
    </div>
  );
}
