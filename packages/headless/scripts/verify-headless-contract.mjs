// Fails when packages/headless/src/contract.ts drifts from the .NET headless
// contract: view names, routes, every request record bound by a headless
// route, the response records (view model, action result, nested DTOs), and
// the credential-type availability rule. flow.ts is held to contract.ts by
// tests/contract.test.ts; types.ts is held to it by compile-time checks in
// contract.ts. Together a C# rename cannot pass CI with a stale SDK.
import fs from "node:fs";
import path from "node:path";
import { packageRoot, readHeadlessContract } from "./contract-source.mjs";

const repoRoot = path.resolve(packageRoot, "../..");
const contractsDir = path.join(repoRoot, "src/SqlOS/AuthServer/Contracts");
const endpointsPath = path.join(repoRoot, "src/SqlOS/AuthServer/Endpoints/HeadlessAuthEndpoints.cs");
const servicePath = path.join(repoRoot, "src/SqlOS/AuthServer/Services/SqlOSHeadlessAuthService.cs");
const rendererPath = path.join(repoRoot, "src/SqlOS/AuthServer/Services/SqlOSAuthPageRenderer.cs");

const errors = [];

function read(file) {
  if (!fs.existsSync(file)) {
    errors.push(`missing ${path.relative(repoRoot, file)}`);
    return "";
  }
  return fs.readFileSync(file, "utf8");
}

function camelCase(name) {
  return name.charAt(0).toLowerCase() + name.slice(1);
}

function splitTopLevel(body) {
  const parts = [];
  let current = "";
  let depth = 0;
  for (const char of body) {
    if (char === "<") {
      depth += 1;
    } else if (char === ">") {
      depth -= 1;
    } else if (char === "," && depth === 0) {
      parts.push(current);
      current = "";
      continue;
    }
    current += char;
  }
  if (current.trim()) {
    parts.push(current);
  }
  return parts;
}

// All C# contract files concatenated: headless records live next to shared
// records (SqlOSOrganizationOption, SqlOSAuthPageSettingsDto, ...).
const contracts = fs
  .readdirSync(contractsDir)
  .filter((name) => name.endsWith(".cs"))
  .sort()
  .map((name) => fs.readFileSync(path.join(contractsDir, name), "utf8"))
  .join("\n");

function extractRecordParams(recordName) {
  const marker = new RegExp(`public sealed record ${recordName}\\(`);
  const start = contracts.search(marker);
  if (start < 0) {
    errors.push(`record ${recordName} not found under src/SqlOS/AuthServer/Contracts`);
    return [];
  }
  const open = contracts.indexOf("(", start);
  let depth = 0;
  let end = -1;
  for (let index = open; index < contracts.length; index += 1) {
    const char = contracts[index];
    if (char === "(") {
      depth += 1;
    }
    if (char === ")") {
      depth -= 1;
      if (depth === 0) {
        end = index;
        break;
      }
    }
  }
  if (end < 0) {
    errors.push(`could not parse ${recordName} parameters`);
    return [];
  }
  return splitTopLevel(contracts.slice(open + 1, end)).map((part) => {
    const noDefault = part.split("=")[0].trim();
    const match = noDefault.match(/([A-Za-z_][A-Za-z0-9_]*)\s*$/);
    return match ? match[1] : "";
  }).filter(Boolean);
}

function assertSame(label, expected, actual) {
  const missing = expected.filter((item) => !actual.includes(item));
  const extra = actual.filter((item) => !expected.includes(item));
  if (missing.length > 0) {
    errors.push(`${label}: package lists ${missing.join(", ")} but the server does not`);
  }
  if (extra.length > 0) {
    errors.push(`${label}: server has ${extra.join(", ")} that the package lacks`);
  }
}

const contract = readHeadlessContract(errors);
const endpoints = read(endpointsPath);
const service = read(servicePath);
const renderer = read(rendererPath);

// --- Response records -------------------------------------------------------

assertSame(
  "SqlOSHeadlessViewModel fields",
  contract.viewModelFields,
  extractRecordParams("SqlOSHeadlessViewModel").map(camelCase),
);
assertSame(
  "SqlOSHeadlessActionResult fields",
  contract.actionResultFields,
  extractRecordParams("SqlOSHeadlessActionResult").map(camelCase),
);
for (const [recordName, fields] of contract.dtoFields) {
  assertSame(`${recordName} fields`, fields, extractRecordParams(recordName).map(camelCase));
}

// --- Routes ----------------------------------------------------------------

const mappedPosts = [...endpoints.matchAll(/headless\.MapPost\("([^"]+)"/g)].map((match) => match[1]);
const mappedGets = [...endpoints.matchAll(/headless\.MapGet\("([^"]+)"/g)].map((match) => match[1]);
assertSame("headless POST paths", contract.actionPaths, mappedPosts);
assertSame("headless GET paths", contract.getPaths, mappedGets);

// --- Request records: route → bound record → camelCased parameters ----------

const boundRequests = new Map(
  [...endpoints.matchAll(/headless\.MapPost\("([^"]+)",\s*async\s*\(\s*([A-Za-z0-9_]+)\s+request\b/g)].map(
    (match) => [match[1], match[2]],
  ),
);
for (const route of mappedPosts) {
  if (!boundRequests.has(route)) {
    errors.push(`${route}: could not find the request record bound by HeadlessAuthEndpoints.cs`);
  }
}
assertSame("HEADLESS_REQUEST_FIELDS routes", [...contract.requestFields.keys()], mappedPosts);
for (const [route, recordName] of boundRequests) {
  const expected = contract.requestFields.get(route);
  if (!expected) {
    continue;
  }
  assertSame(`${route} (${recordName}) fields`, expected, extractRecordParams(recordName).map(camelCase));
}

// --- Views -----------------------------------------------------------------

const viewCases = [...service.matchAll(/"([a-z0-9-]+)"\s*=>\s*"\1"/g)].map((match) => match[1]);
for (const view of contract.views.filter((view) => view !== "login")) {
  if (!viewCases.includes(view)) {
    errors.push(`NormalizeView is missing ${view}`);
  }
}
for (const view of viewCases) {
  if (!contract.views.includes(view)) {
    errors.push(`HEADLESS_VIEWS is missing ${view}`);
  }
}

if (!contract.actionResultTypes.includes("view") || !contract.actionResultTypes.includes("redirect")) {
  errors.push("HEADLESS_ACTION_RESULT_TYPES must include view and redirect");
}

// --- Credential availability rule (hosted renderer is the reference) --------

const rendererRules = new Map(
  [...renderer.matchAll(
    /model\.Settings\.([A-Za-z0-9_]+)\s*&&\s*SupportsCredentialType\(model\.Settings\.EnabledCredentialTypes,\s*"([a-z_]+)"\)/g,
  )].map((match) => [match[2], camelCase(match[1])]),
);
if (rendererRules.size === 0) {
  errors.push("SqlOSAuthPageRenderer.cs: could not find the SupportsCredentialType rules");
}
assertSame("HEADLESS_CREDENTIAL_TYPES", contract.credentialTypes, [...rendererRules.keys()]);
for (const [type, flag] of rendererRules) {
  const packageFlag = contract.credentialRuntimeFlags.get(type);
  if (packageFlag !== flag) {
    errors.push(`HEADLESS_CREDENTIAL_RUNTIME_FLAGS[${type}]: package has ${packageFlag ?? "nothing"}, server uses ${flag}`);
  }
}
const settingsFields = contract.dtoFields.get("SqlOSAuthPageSettingsDto") ?? [];
for (const flag of contract.credentialRuntimeFlags.values()) {
  if (!settingsFields.includes(flag)) {
    errors.push(`HEADLESS_CREDENTIAL_RUNTIME_FLAGS points at ${flag}, which is not a SqlOSAuthPageSettingsDto field`);
  }
}

if (errors.length > 0) {
  console.error(`Headless contract drift:\n- ${errors.join("\n- ")}`);
  process.exit(1);
}

console.log(
  `Headless contract matches the server: ${contract.actionPaths.length} routes, ${contract.requestFields.size} request records, ${contract.dtoFields.size + 2} response records, ${contract.views.length} views, ${contract.credentialTypes.length} credential types.`,
);
