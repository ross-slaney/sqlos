import { execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const repoRoot = path.resolve(path.dirname(scriptPath), "..");
const snippetSpecs = [
  {
    name: "README first-run program",
    relativePath: "README.md",
    heading: "### Add SqlOS to an application",
    marker: "builder.AddSqlOS<AppDbContext>",
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
    marker: "var filter = await fga.GetAuthorizationFilterAsync<Workspace>",
    wrap: asFgaGroupListProgram,
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

const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sqlos-doc-snippets-"));
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
    <ProjectReference Include="${sourceProject}" />
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
