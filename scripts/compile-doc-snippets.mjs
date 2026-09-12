import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const repoRoot = path.resolve(path.dirname(scriptPath), "..");
const snippetSpecs = [
  {
    name: "standalone multi-client host", relativePath: "web/content/docs/guides/standalone-identity-server.mdx",
    heading: "## 1. Configure the auth host", marker: "var builder = WebApplication.CreateBuilder(args);",
    wrap: asCompleteProgram,
  },
  ...[
    ["web/content/docs/guides/standalone-identity-server.mdx", "## 5. Validate tokens in resource APIs"],
    ["web/content/docs/reference/standalone-identity-server.mdx", "## Resource API validation"],
  ].map(([relativePath, heading]) => ({
    name: "separate resource API", relativePath, heading,
    marker: "builder.Services.AddAuthentication",
    wrap: (snippet) => `var builder = WebApplication.CreateBuilder(args);\n${snippet}`,
  })),
  {
    name: "multiple-application migration", relativePath: "web/content/docs/authserver/multiple-applications.mdx",
    heading: "## Graduate an existing application", marker: "builder.AddSqlOS<AppDbContext>",
    wrap: (snippet) => snippet.replace("builder.AddSqlOS<AppDbContext>", `using SqlOS;
var builder = WebApplication.CreateBuilder(args);
var connectionString = "Server=localhost;Database=acme;Integrated Security=True;TrustServerCertificate=True";
builder.AddSqlOS<AppDbContext>`) + `
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : SqlOSDbContext<AppDbContext>(options);
`,
  },
  ...[
    "### Complete identity-provider host",
    "### Complete downstream OIDC application",
  ].map((heading) => ({
    name: "README complete OIDC program",
    relativePath: "README.md", heading,
    marker: "var builder = WebApplication.CreateBuilder(args);",
    wrap: asCompleteProgram,
  })),
  {
    name: "README MCP registration", relativePath: "README.md",
    heading: "### MCP: resource settings and the Microsoft SDK",
    marker: "builder.AddSqlOS<NotesDbContext>",
    wrap: (snippet) => snippet.replace("builder.Services.AddScoped", 'var builder = WebApplication.CreateBuilder(args);\nvar connectionString = "Server=localhost;Database=notes;Integrated Security=True;TrustServerCertificate=True";\nbuilder.Services.AddScoped'),
  },
  {
    name: "README MCP tools", relativePath: "README.md",
    heading: "### MCP: resource settings and the Microsoft SDK",
    marker: "public sealed class NotesMcpTools",
    wrap: (snippet) => snippet.replace("public sealed class NotesMcpTools", "var builder = WebApplication.CreateBuilder(args);\nbuilder.Build().Run();\n\npublic sealed class NotesMcpTools"),
  },
  {
    name: "README FGA service", relativePath: "README.md",
    heading: "### `app.Authorization(...)`: vocabulary, grants, and enforcement",
    marker: "public async Task<IReadOnlyList<Note>> ListAsync",
    wrap: (snippet) => `using Microsoft.EntityFrameworkCore;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.OneCall.Api;
var builder = WebApplication.CreateBuilder(args);
builder.Build().Run();
public sealed class DocumentedNotesService(NotesDbContext db, ISqlOSFgaAuthService fga)
{
${snippet}
${extractCsharpBlock({ relativePath: "README.md", heading: "### `app.Authorization(...)`: vocabulary, grants, and enforcement", marker: "private async Task CreateNotebookIfMissingAsync" })}
}
`,
  },
  {
    name: "README branding", relativePath: "README.md",
    heading: "## `app.Brand(...)`: hosted pages and ownership", marker: "app.Brand(page =>",
    wrap: (snippet) => `using SqlOS.Configuration;
new SqlOSOptions().UseSingleApplication("Acme", app => { ${snippet} });`,
  },
  {
    name: "README SCIM administration", relativePath: "README.md",
    heading: "### SCIM: provision into SqlOS", marker: "await using var scope",
    wrap: (snippet) => snippet.replace("await using var scope", 'var app = WebApplication.CreateBuilder(args).Build();\nvar organizationId = "org_acme";\nawait using var scope'),
  },
  {
    name: "README first-run program",
    relativePath: "README.md",
    heading: "### Add it to a project",
    marker: "var builder = WebApplication.CreateBuilder(args);",
    wrap: asCompleteProgram,
  },
  ...[
    "web/content/docs/quickstarts/add-to-app.mdx",
    "web/content/docs/quickstarts/protect-api.mdx",
    "web/content/docs/quickstarts/ef-authorization.mdx",
  ].map((relativePath) => ({
    name: "quickstart complete program",
    relativePath,
    heading: "## Complete program",
    marker: "var builder = WebApplication.CreateBuilder(args);",
    wrap: asCompleteProgram,
  })),
  {
    name: "hierarchical EF authorization blog complete program",
    relativePath:
      "web/content/blog/hierarchical-authorization-native-ef-core.mdx",
    heading: "## A complete runnable API host",
    marker: "var builder = WebApplication.CreateBuilder(args);",
    wrap: asCompleteProgram,
  },
  {
    name: "code-owned OAuth clients blog complete program",
    relativePath:
      "web/content/blog/your-oauth-clients-belong-in-your-codebase.mdx",
    heading: "## Declare the topology once",
    marker: "builder.AddSqlOS<AppDbContext>",
    wrap: asCompleteProgram,
  },
  {
    name: "audit-log registration",
    relativePath: "web/content/docs/reference/audit-logs.mdx",
    heading: "## Service registration",
    marker: "builder.AddSqlOS<ExampleAppDbContext>",
    wrap: asAuditLogRegistrationProgram,
  },
  {
    name: "audit-log record endpoint",
    relativePath: "web/content/docs/reference/audit-logs.mdx",
    heading: "## Service registration",
    marker: "ISqlOSAuditLogService auditLogs",
    wrap: asAuditLogRecordProgram,
  },
  {
    name: "phone OTP host configuration",
    relativePath: "web/content/docs/reference/authserver-api.mdx",
    heading: "### Phone OTP sign-in and signup",
    marker: "options.AuthServer.ConfigurePhoneOtp",
    wrap: asPhoneOtpConfigurationProgram,
  },
  {
    name: "security-settings update",
    relativePath: "web/content/docs/reference/authserver-api.mdx",
    heading: "### UpdateSecuritySettingsAsync",
    marker: "new SqlOSUpdateSecuritySettingsRequest(",
    wrap: asSecuritySettingsProgram,
  },
  {
    name: "FGA group grant",
    relativePath: "web/content/docs/guides/fga-groups.mdx",
    heading: "## 4. Grant one role to the group",
    marker: "await db.GrantRoleAsync(",
    wrap: asFgaGroupGrantProgram,
  },
  {
    name: "FGA group detail authorization",
    relativePath: "web/content/docs/guides/fga-groups.mdx",
    heading: "## 5. Prove inherited detail access",
    marker: "var workspace = await db.Workspaces",
    wrap: asFgaGroupDetailProgram,
  },
  {
    name: "FGA group list authorization",
    relativePath: "web/content/docs/guides/fga-groups.mdx",
    heading: "## 6. Prove inherited list access",
    marker: "var filter = await fga.BuildFilterAsync<Workspace>",
    wrap: asFgaGroupListProgram,
  },
  {
    name: "code-first application access policy",
    relativePath: "web/content/docs/guides/multi-app-access.mdx",
    heading: "## 1. Register a client per application",
    marker: "client.AssignOrganization(\"northwind-subscription\"",
    wrap: (snippet) => snippet.replace("builder.AddSqlOS<AppDbContext>", `using Microsoft.EntityFrameworkCore;
using SqlOS;
using SqlOS.AuthServer.Contracts;
using SqlOS.Extensions;
var builder = WebApplication.CreateBuilder(args);
var connectionString = "Server=localhost;Database=atlas;Integrated Security=True;TrustServerCertificate=True";
builder.AddSqlOS<AppDbContext>`) + `
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : SqlOSDbContext<AppDbContext>(options);
`,
  },
  {
    name: "platform-admin session revocation",
    relativePath: "web/content/docs/authserver/sessions-and-tokens.mdx",
    heading: "### Platform-admin revocation",
    marker: "revocations.PreviewAsync(",
    wrap: asSessionRevocationProgram,
  },
  {
    name: "code-first SAML connection",
    relativePath: "web/content/docs/authserver/saml-sso.mdx",
    heading: "### Code-first connection",
    marker: "SeedSamlConnection(",
    wrap: asSamlSeedProgram,
  },
  {
    name: "unified machine-client seed",
    relativePath: "web/content/docs/guides/service-account-jobs.mdx",
    heading: "## Recommended: declare one machine client",
    marker: "SeedMachineClient(",
    wrap: asMachineClientSeedProgram,
  },
  {
    name: "3.24 machine-client release example",
    relativePath: "web/content/blog/sqlos-3-24-three-control-planes-one-auth-system.mdx",
    heading: "## One machine client instead of four disconnected records",
    marker: "SeedMachineClient(",
    wrap: asMachineClientSeedProgram,
  },
];

function extractCsharpBlock({ relativePath, heading, marker }) {
  const markdown = fs.readFileSync(path.join(repoRoot, relativePath), "utf8");
  const lines = markdown.split(/\r?\n/);
  const headingLines = lines
    .map((line, index) => (line === heading ? index : -1))
    .filter((index) => index !== -1);

  if (headingLines.length !== 1) {
    throw new Error(
      `${relativePath}: expected exactly one '${heading}' heading, found ${headingLines.length}.`,
    );
  }

  const headingPrefix = /^(#{1,6})\s+/.exec(heading);
  if (!headingPrefix) {
    throw new Error(`Invalid snippet heading '${heading}'.`);
  }

  const headingLevel = headingPrefix[1].length;
  const sectionStart = headingLines[0] + 1;
  let sectionEnd = lines.length;

  for (let index = sectionStart; index < lines.length; index += 1) {
    const nextHeading = /^(#{1,6})\s+/.exec(lines[index]);
    if (nextHeading && nextHeading[1].length <= headingLevel) {
      sectionEnd = index;
      break;
    }
  }

  const blocks = [];
  for (let index = sectionStart; index < sectionEnd; index += 1) {
    if (lines[index].trim() !== "```csharp") {
      continue;
    }

    const blockStart = index + 1;
    index = blockStart;
    while (index < sectionEnd && lines[index].trim() !== "```") {
      index += 1;
    }

    if (index === sectionEnd) {
      throw new Error(`${relativePath}: unterminated C# block under '${heading}'.`);
    }

    blocks.push(lines.slice(blockStart, index).join("\n"));
  }

  const matchingBlocks = blocks.filter((block) => block.includes(marker));
  if (matchingBlocks.length !== 1) {
    throw new Error(
      `${relativePath}: expected exactly one C# block containing '${marker}' under '${heading}', found ${matchingBlocks.length}.`,
    );
  }

  const markerOccurrences = matchingBlocks[0].split(marker).length - 1;
  if (markerOccurrences !== 1) {
    throw new Error(
      `${relativePath}: expected '${marker}' once in its C# block, found ${markerOccurrences}.`,
    );
  }

  return matchingBlocks[0];
}

function asCompleteProgram(snippet) {
  return `${snippet}\n`;
}

function asAuditLogRegistrationProgram(snippet) {
  return `using Microsoft.EntityFrameworkCore;
using SqlOS;
using SqlOS.Extensions;

var builder = WebApplication.CreateBuilder(args);
var connectionString = "Server=localhost;Database=Example;Trusted_Connection=True";

${snippet}

public sealed class ExampleAppDbContext(DbContextOptions<ExampleAppDbContext> options)
    : SqlOSDbContext<ExampleAppDbContext>(options)
{
}
`;
}

function asAuditLogRecordProgram(snippet) {
  return `using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Extensions;
using SqlOS.Extensions;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var apiAudience = "https://api.example.com";

${snippet}

public sealed class WorkspaceDbContext : DbContext
{
    public DbSet<WorkspaceDocument> Documents => Set<WorkspaceDocument>();
}

public sealed class WorkspaceDocument
{
    public string Id { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SharedRole { get; set; } = string.Empty;
}
`;
}

function asPhoneOtpConfigurationProgram(snippet) {
  return `using SqlOS.Configuration;

var builder = WebApplication.CreateBuilder(args);
var options = new SqlOSOptions();

${snippet}
`;
}

function asSecuritySettingsProgram(snippet) {
  return `using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;

SqlOSSettingsService settingsService = null!;

${snippet}
`;
}

function asSessionRevocationProgram(snippet) {
  return `using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;

SqlOSSessionRevocationService revocations = null!;
var organizationId = "org_acme";
var clientApplicationId = "client_portal";
var ct = CancellationToken.None;

${snippet}
`;
}

function asSamlSeedProgram(snippet) {
  return `using Microsoft.Extensions.Configuration;
using SqlOS.AuthServer.Configuration;
using SqlOS.Configuration;

var builder = WebApplication.CreateBuilder(args);
var options = new SqlOSOptions();

${snippet}
`;
}

function asMachineClientSeedProgram(snippet) {
  return `using Microsoft.Extensions.Configuration;
using SqlOS.AuthServer.Configuration;
using SqlOS.Configuration;

var builder = WebApplication.CreateBuilder(args);
var options = new SqlOSOptions();

${snippet}
`;
}

function asFgaGroupGrantProgram(snippet) {
  return `using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

ISqlOSFgaDbContext db = null!;
var support = new SqlOSFgaUserGroup();
var ct = CancellationToken.None;

${snippet}
`;
}

function asFgaGroupDetailProgram(snippet) {
  return `using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

static async Task<IResult> AuthorizeDetailAsync(
    ReviewDbContext db,
    ISqlOSFgaAuthService fga,
    string workspaceId,
    string organizationId,
    string userSubjectId,
    CancellationToken ct)
{
${snippet}
}

public sealed class ReviewDbContext(DbContextOptions<ReviewDbContext> options) : DbContext(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
}

public sealed class Workspace : IHasResourceId
{
    public string Id { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
`;
}

function asFgaGroupListProgram(snippet) {
  return `using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

ISqlOSFgaAuthService fga = null!;
ReviewDbContext db = null!;
var userSubjectId = "subj_user";
var organizationId = "org_acme";
var ct = CancellationToken.None;

${snippet}

public sealed class ReviewDbContext(DbContextOptions<ReviewDbContext> options) : DbContext(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
}

public sealed class Workspace : IHasResourceId
{
    public string Id { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
`;
}

// Keep project references on the same canonical filesystem tree. macOS temp-directory
// symlinks can otherwise make MSBuild compute invalid transitive relative paths.
const artifactsRoot = path.join(repoRoot, "artifacts");
fs.mkdirSync(artifactsRoot, { recursive: true });
const tempRoot = fs.mkdtempSync(path.join(artifactsRoot, "sqlos-doc-snippets-"));
const projectPath = path.join(tempRoot, "SqlOS.Docs.Snippet.csproj");
const sourceProject = path.join(repoRoot, "src", "SqlOS", "SqlOS.csproj");

try {
  fs.writeFileSync(
    projectPath,
    `<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="9.0.0" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.OpenIdConnect" Version="9.0.0" />
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="2.2.0" />
    <ProjectReference Include="${sourceProject}" />
    <ProjectReference Include="${path.join(repoRoot, "examples", "SqlOS.OneCall.Api", "SqlOS.OneCall.Api.csproj")}" />
  </ItemGroup>
</Project>
`,
  );

  let restored = false;
  let failures = 0;
  for (const spec of snippetSpecs) {
    const description = `${spec.relativePath} (${spec.name})`;

    try {
      const snippet = extractCsharpBlock(spec);
      fs.writeFileSync(path.join(tempRoot, "Program.cs"), spec.wrap(snippet));

      const args = ["build", projectPath, "--nologo", "--verbosity", "minimal"];
      if (restored) {
        args.push("--no-restore");
      }

      execFileSync("dotnet", args, {
        cwd: repoRoot,
        encoding: "utf8",
        stdio: "pipe",
      });
      restored = true;
      console.log(`Compiled documentation snippet: ${description}`);
    } catch (error) {
      const stdout = error.stdout?.toString() ?? "";
      const stderr = error.stderr?.toString() ?? "";
      console.error(`${description}: documentation snippet check failed.\n`);
      if (stdout || stderr) {
        console.error(stdout);
        console.error(stderr);
      } else {
        console.error(error.message ?? error);
      }
      failures += 1;
    }
  }

  if (failures > 0) {
    process.exitCode = 1;
  }
} finally {
  fs.rmSync(tempRoot, { recursive: true, force: true });
}
