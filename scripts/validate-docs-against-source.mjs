import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";
import { readHeadlessContract } from "../packages/headless/scripts/contract-source.mjs";

const scriptPath = fileURLToPath(import.meta.url);
const repoRoot = path.resolve(path.dirname(scriptPath), "..");
const projectPath = path.join(repoRoot, "src", "SqlOS", "SqlOS.csproj");
const referenceRoot = path.join(repoRoot, "web", "content", "docs", "reference");
const staleBlogPath = path.join(
  repoRoot,
  "web",
  "content",
  "blog",
  "row-level-security-ef-core-sql-server-tvfs.mdx",
);

function read(relativePath) {
  return fs.readFileSync(path.join(repoRoot, relativePath), "utf8");
}

function requireMatch(content, pattern, message, errors) {
  if (!pattern.test(content)) {
    errors.push(message);
  }
}

const errors = [];
const project = fs.readFileSync(projectPath, "utf8");
const packageVersion = project.match(/<Version>([^<]+)<\/Version>/)?.[1];
const targetFramework = project.match(/<TargetFramework>([^<]+)<\/TargetFramework>/)?.[1];

if (!packageVersion) {
  errors.push("src/SqlOS/SqlOS.csproj: could not read <Version>.");
}

if (!targetFramework) {
  errors.push("src/SqlOS/SqlOS.csproj: could not read <TargetFramework>.");
}

requireMatch(
  project,
  /<GenerateDocumentationFile>true<\/GenerateDocumentationFile>/,
  "src/SqlOS/SqlOS.csproj: XML documentation generation must remain enabled.",
  errors,
);

const referenceFiles = fs
  .readdirSync(referenceRoot)
  .filter((name) => name.endsWith(".mdx"))
  .sort();
const referenceContents = referenceFiles
  .map((name) => fs.readFileSync(path.join(referenceRoot, name), "utf8"))
  .join("\n");
const index = fs.readFileSync(path.join(referenceRoot, "index.mdx"), "utf8");
const repositoryReadme = read("README.md");
const addToAppQuickstart = read("web/content/docs/quickstarts/add-to-app.mdx");

if (packageVersion && !index.includes(`SqlOS ${packageVersion}`)) {
  errors.push(
    `web/content/docs/reference/index.mdx: expected current package marker 'SqlOS ${packageVersion}'.`,
  );
}

if (packageVersion && !repositoryReadme.includes(`--version ${packageVersion}`)) {
  errors.push(
    `README.md: expected package install command for SqlOS ${packageVersion}.`,
  );
}

if (packageVersion && !repositoryReadme.includes(`npm install @sqlos/headless@${packageVersion}`)) {
  errors.push(
    `README.md: expected npm install command for @sqlos/headless@${packageVersion}.`,
  );
}

if (packageVersion) {
  const headlessPackage = JSON.parse(read("packages/headless/package.json"));
  if (headlessPackage.name !== "@sqlos/headless") {
    errors.push("packages/headless/package.json: name must be @sqlos/headless.");
  }
  if (headlessPackage.version !== packageVersion) {
    errors.push(
      `packages/headless/package.json: version ${headlessPackage.version} must match SqlOS ${packageVersion}.`,
    );
  }
}

if (packageVersion && !addToAppQuickstart.includes(`--version ${packageVersion}`)) {
  errors.push(
    `web/content/docs/quickstarts/add-to-app.mdx: expected package install command for SqlOS ${packageVersion}.`,
  );
}

const headlessJsReference = read("web/content/docs/reference/headless-js.mdx");
if (packageVersion && !headlessJsReference.includes(`@sqlos/headless@${packageVersion}`)) {
  errors.push(
    `web/content/docs/reference/headless-js.mdx: expected npm install for @sqlos/headless@${packageVersion}.`,
  );
}

if (targetFramework && !index.includes(`\`${targetFramework}\``)) {
  errors.push(
    `web/content/docs/reference/index.mdx: expected current target framework marker '${targetFramework}'.`,
  );
}

const requiredSourceContracts = [
  [
    "src/SqlOS/SqlOSDbContext.cs",
    /public abstract class SqlOSDbContext<TContext>/,
    "SqlOSDbContext<TContext>",
  ],
  [
    "src/SqlOS/Extensions/WebApplicationBuilderExtensions.cs",
    /public static WebApplicationBuilder AddSqlOS<TContext>/,
    "WebApplicationBuilder.AddSqlOS<TContext>",
  ],
  [
    "src/SqlOS/Extensions/WebApplicationExtensions.cs",
    /public static WebApplication MapSqlOS/,
    "WebApplication.MapSqlOS",
  ],
  [
    "src/SqlOS/Extensions/SqlOSErgonomicsExtensions.cs",
    /public static RouteGroupBuilder RequireSqlOSAccessToken/,
    "RouteGroupBuilder.RequireSqlOSAccessToken",
  ],
  [
    "src/SqlOS/AuthServer/Extensions/SqlOSAccessTokenValidationExtensions.cs",
    /public static SqlOSValidatedToken\? GetSqlOSValidatedToken/,
    "HttpContext.GetSqlOSValidatedToken",
  ],
  [
    "src/SqlOS/Fga/Interfaces/ISqlOSFgaAuthService.cs",
    /Task<Expression<Func<T, bool>>> BuildFilterAsync<T>/,
    "ISqlOSFgaAuthService.BuildFilterAsync<T>",
  ],
  [
    "src/SqlOS/AuthServer/Configuration/SqlOSAuthServerOptions.cs",
    /public SqlOSAuthServerOptions ConfigurePhoneOtp/,
    "SqlOSAuthServerOptions.ConfigurePhoneOtp",
  ],
  [
    "src/SqlOS/AuthServer/Services/SqlOSAuthService.cs",
    /public async Task<SqlOSPhoneOtpStartResult> RequestPhoneOtpAsync/,
    "SqlOSAuthService.RequestPhoneOtpAsync",
  ],
  [
    "src/SqlOS/AuthServer/Services/SqlOSAuthService.cs",
    /public async Task<SqlOSLoginResult> VerifyPhoneOtpAsync/,
    "SqlOSAuthService.VerifyPhoneOtpAsync",
  ],
  [
    "examples/SqlOS.Example.AspNetCoreWeb/Program.cs",
    /\.AddOpenIdConnect\("SqlOS", options =>[\s\S]*options\.UsePkce = true;[\s\S]*options\.SaveTokens = true;/,
    "ASP.NET Core OpenID Connect + PKCE example",
  ],
  [
    "examples/SqlOS.Todo.Api/Program.cs",
    /ClientId = "example-aspnet"[\s\S]*http:\/\/localhost:5090\/signin-sqlos/,
    "Todo ASP.NET Core public client registration",
  ],
  [
    "examples/SqlOS.Example.Tests/AspNetCoreWebSessionTests.cs",
    /ExpiringAccessToken_RotatesRefreshToken_RenewsTicket_AndCallsApi/,
    "ASP.NET Core session refresh regression test",
  ],
  [
    "examples/SqlOS.Todo.AppHost/SqlOS.Todo.AppHost.csproj",
    /<UserSecretsId>sqlos-todo-apphost<\/UserSecretsId>/,
    "Todo AppHost user-secrets support",
  ],
  [
    "src/SqlOS/AuthServer/Services/SqlOSAuthorizationServerService.cs",
    /input\.State is \{ Length: > 2048 \}/,
    "OAuth state length validation",
  ],
  [
    "src/SqlOS/Hosting/SqlOSPipelineStartupFilter.cs",
    /app\.UseForwardedHeaders\(\);[\s\S]*UseMiddleware<RootDashboardMiddleware>/,
    "trusted forwarded headers before dashboard middleware",
  ],
  [
    "src/SqlOS/AuthServer/Services/SqlOSCimdClientService.cs",
    /await EnforceFetchPolicyAsync\(clientIdUri, clientId, cancellationToken\);[\s\S]*TrustedHosts\.Count > 0/,
    "CIMD pre-fetch host allowlist",
  ],
  [
    "src/SqlOS/AuthServer/Services/SqlOSCimdHttpHandlerFactory.cs",
    /AllowAutoRedirect = false,[\s\S]*UseProxy = false,[\s\S]*ConnectCallback/,
    "CIMD redirect-disabled HTTP client",
  ],
];

for (const [relativePath, pattern, contractName] of requiredSourceContracts) {
  requireMatch(
    read(relativePath),
    pattern,
    `${relativePath}: documented contract '${contractName}' was not found. Update the reference docs with the source change.`,
    errors,
  );
}

const oauthStateSqlServer = read("src/SqlOS/AuthServer/Schema/SqlServer/027_OAuthStateLength.sql");
const widenedStateColumns = oauthStateSqlServer.match(
  /ALTER COLUMN \[State\] NVARCHAR\(2048\) NOT NULL;/g,
);
const stateWideningGuards = oauthStateSqlServer.match(
  /COL_LENGTH\('[^']+', 'State'\) < 4096/g,
);
if (widenedStateColumns?.length !== 2 || stateWideningGuards?.length !== 2) {
  errors.push(
    "src/SqlOS/AuthServer/Schema/SqlServer/027_OAuthStateLength.sql: both OAuth state columns must widen for ASP.NET Core protected state without narrowing larger operator hotfixes.",
  );
}

const oauthStatePostgreSql = read("src/SqlOS/AuthServer/Schema/PostgreSql/027_OAuthStateLength.sql");
const widenedStateColumnsPg = oauthStatePostgreSql.match(
  /ALTER COLUMN "State" TYPE varchar\(2048\);/g,
);
const stateWideningGuardsPg = oauthStatePostgreSql.match(
  /character_maximum_length < 2048/g,
);
if (widenedStateColumnsPg?.length !== 2 || stateWideningGuardsPg?.length !== 2) {
  errors.push(
    "src/SqlOS/AuthServer/Schema/PostgreSql/027_OAuthStateLength.sql: both OAuth state columns must widen for ASP.NET Core protected state without narrowing larger operator hotfixes.",
  );
}

const checkedDocs = `${referenceContents}\n${fs.readFileSync(staleBlogPath, "utf8")}`;
const stalePatterns = [
  [/new\s+SqlOSSignupRequest\(\s*email\s*,\s*password\s*,\s*displayName\s*,\s*clientId\s*\)/, "stale SqlOSSignupRequest constructor"],
  [/new\s+SqlOSExchangeCodeRequest\(\s*code\s*,\s*state\s*\)/, "stale SqlOSExchangeCodeRequest constructor"],
  [/new\s+SqlOSCreateVerificationTokenRequest\(\s*userId\s*,\s*email\s*\)/, "stale SqlOSCreateVerificationTokenRequest constructor"],
  [/new\s+(?:CreateOrganizationRequest|CreateUserRequest|CreateMembershipRequest|CreateClientRequest|CreateSsoConnectionDraftRequest)\b/, "contract missing the SqlOS prefix"],
  [/\bPagedResult<T>\b/, "stale PagedResult<T> type name"],
  [/\bresult\.Items\b/, "stale pagination Items property"],
  [/\bresult\.HasMore\b/, "stale pagination HasMore property"],
  [/\bGetAuthorizationFilterAsync\b/, "stale FGA GetAuthorizationFilterAsync method"],
  [/\bSqlOSSsoConnectionDraft\b/, "nonexistent SqlOSSsoConnectionDraft return type"],
  [/\bSqlOSOidcProviderInfo\b/, "nonexistent SqlOSOidcProviderInfo return type"],
  [/\bSqlOSHomeRealmDiscoveryResponse\b/, "nonexistent SqlOSHomeRealmDiscoveryResponse type"],
  [/\boptions\.UseSqlServer\(/, "nonexistent SqlOSOptions.UseSqlServer configuration"],
  [/["']password["']\s*\|\s*["']sso["']\s*\|\s*["']oidc["']/, "unsupported Home Realm Discovery oidc mode"],
  [/\boidc_google\b/, "unsupported OIDC authentication method value"],
  [/Join existing org instead of creating one\./i, "unsafe public-signup existing-organization claim"],
  [/can only log in via SSO\/OIDC/i, "incomplete passwordless-login claim"],
  [/Next\.js,\s*Angular,\s*and\s*Expo/i, "unlaunched Expo example-stack claim"],
];

for (const [pattern, description] of stalePatterns) {
  if (pattern.test(checkedDocs)) {
    errors.push(`reference docs: found ${description}.`);
  }
}

const httpReference = fs.readFileSync(
  path.join(referenceRoot, "api-reference.mdx"),
  "utf8",
);
if (/^## Auth API \(Example\)/m.test(httpReference)) {
  errors.push(
    "web/content/docs/reference/api-reference.mdx: example-app routes must not be presented as SqlOS library endpoints.",
  );
}

// Headless view names in docs must exist in packages/headless HEADLESS_VIEWS,
// and every HEADLESS_VIEWS entry must be documented. The contract file is read
// through the package's own parser so there is one source of truth.
{
  const headlessErrors = [];
  const headlessContract = readHeadlessContract(headlessErrors);
  for (const message of headlessErrors) {
    errors.push(`packages/headless/src/contract.ts: ${message}`);
  }
  const headlessViews = new Set(headlessContract.views);
  const headlessDocRoots = [
    path.join(repoRoot, "web", "content", "docs", "guides"),
    path.join(repoRoot, "web", "content", "docs", "authserver"),
    path.join(repoRoot, "web", "content", "docs", "reference"),
  ];

  function walkMdx(dir, files = []) {
    if (!fs.existsSync(dir)) return files;
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) walkMdx(full, files);
      else if (entry.name.endsWith(".mdx")) files.push(full);
    }
    return files;
  }

  // A view name counts as cited only in an explicit view position, so ordinary
  // hyphenated prose (`device-code`, `redirect-uri`) never needs a denylist:
  //   view === "x" | view: "x" | case "x": | ?view=x / &view=x
  const viewCitation =
    /(?:\bview\s*(?:===|==|!==|!=|:)\s*["'`]([a-z][a-z0-9-]*)["'`])|(?:\bcase\s+["'`]([a-z][a-z0-9-]*)["'`]\s*:)|(?:[?&]view=([a-z][a-z0-9-]*))/g;

  const documented = new Set();
  for (const root of headlessDocRoots) {
    for (const file of walkMdx(root)) {
      const content = fs.readFileSync(file, "utf8");
      if (!/headless|@sqlos\/headless|HEADLESS_VIEWS/i.test(content)) continue;
      const relative = path.relative(repoRoot, file);
      for (const match of content.matchAll(viewCitation)) {
        const name = match[1] ?? match[2] ?? match[3];
        if (headlessViews.has(name)) {
          documented.add(name);
          continue;
        }
        errors.push(
          `${relative}: headless docs cite view \`${name}\` which is not in HEADLESS_VIEWS.`,
        );
      }
      // Backticked exact matches (for example a views table) also count as documentation.
      for (const match of content.matchAll(/`([a-z][a-z0-9-]*)`/g)) {
        if (headlessViews.has(match[1])) documented.add(match[1]);
      }
    }
  }
  for (const view of headlessViews) {
    if (!documented.has(view)) {
      errors.push(
        `web/content/docs: HEADLESS_VIEWS entry \`${view}\` is not documented in any headless guide, authserver, or reference page.`,
      );
    }
  }
}

if (errors.length > 0) {
  console.error("Documentation/source drift check failed:\n");
  for (const error of errors) {
    console.error(`- ${error}`);
  }
  process.exit(1);
}

console.log(
  `Validated reference docs against SqlOS ${packageVersion} (${targetFramework}) source contracts.`,
);
