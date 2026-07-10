import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

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

if (packageVersion && !index.includes(`SqlOS ${packageVersion}`)) {
  errors.push(
    `web/content/docs/reference/index.mdx: expected current package marker 'SqlOS ${packageVersion}'.`,
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
    "src/SqlOS/Fga/Specifications/PaginatedResult.cs",
    /public class PaginatedResult<T>/,
    "PaginatedResult<T>",
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
];

for (const [relativePath, pattern, contractName] of requiredSourceContracts) {
  requireMatch(
    read(relativePath),
    pattern,
    `${relativePath}: documented contract '${contractName}' was not found. Update the reference docs with the source change.`,
    errors,
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
  [/\bBuildFilterAsync\b/, "stale FGA BuildFilterAsync method"],
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
