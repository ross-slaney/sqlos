import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const packageRoot = path.resolve(scriptDir, "..");
const repoRoot = path.resolve(packageRoot, "../..");
const contractsPath = path.join(repoRoot, "src/SqlOS/AuthServer/Contracts/SqlOSHeadlessAuthContracts.cs");
const endpointsPath = path.join(repoRoot, "src/SqlOS/AuthServer/Endpoints/HeadlessAuthEndpoints.cs");
const servicePath = path.join(repoRoot, "src/SqlOS/AuthServer/Services/SqlOSHeadlessAuthService.cs");
const contractTsPath = path.join(packageRoot, "src/contract.ts");

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

function extractRecordParams(source, recordName) {
  const marker = `public sealed record ${recordName}(`;
  const start = source.indexOf(marker);
  if (start < 0) {
    errors.push(`record ${recordName} not found`);
    return [];
  }
  let depth = 0;
  let end = -1;
  for (let index = start + marker.length - 1; index < source.length; index += 1) {
    const char = source[index];
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
  return splitTopLevel(source.slice(start + marker.length, end)).map((part) => {
    const noDefault = part.split("=")[0].trim();
    const match = noDefault.match(/([A-Za-z_][A-Za-z0-9_]*)\s*$/);
    return match ? match[1] : "";
  }).filter(Boolean);
}

function extractTsArray(source, exportName) {
  const match = source.match(new RegExp(`export const ${exportName} = \\[([\\s\\S]*?)\\] as const`));
  if (!match) {
    errors.push(`export ${exportName} not found in contract.ts`);
    return [];
  }
  return [...match[1].matchAll(/"([^"]+)"/g)].map((item) => item[1]);
}

const contractTs = read(contractTsPath);
const HEADLESS_VIEWS = extractTsArray(contractTs, "HEADLESS_VIEWS");
const HEADLESS_ACTION_PATHS = extractTsArray(contractTs, "HEADLESS_ACTION_PATHS");
const HEADLESS_GET_PATHS = extractTsArray(contractTs, "HEADLESS_GET_PATHS");
const HEADLESS_VIEW_MODEL_FIELDS = extractTsArray(contractTs, "HEADLESS_VIEW_MODEL_FIELDS");
const HEADLESS_ACTION_RESULT_FIELDS = extractTsArray(contractTs, "HEADLESS_ACTION_RESULT_FIELDS");
const HEADLESS_ACTION_RESULT_TYPES = extractTsArray(contractTs, "HEADLESS_ACTION_RESULT_TYPES");

const contracts = read(contractsPath);
const endpoints = read(endpointsPath);
const service = read(servicePath);

function assertSame(label, expected, actual) {
  const missing = expected.filter((item) => !actual.includes(item));
  const extra = actual.filter((item) => !expected.includes(item));
  if (missing.length > 0) {
    errors.push(`${label}: package missing ${missing.join(", ")}`);
  }
  if (extra.length > 0) {
    errors.push(`${label}: package extra ${extra.join(", ")}`);
  }
}

assertSame(
  "SqlOSHeadlessViewModel fields",
  HEADLESS_VIEW_MODEL_FIELDS,
  extractRecordParams(contracts, "SqlOSHeadlessViewModel").map(camelCase),
);
assertSame(
  "SqlOSHeadlessActionResult fields",
  HEADLESS_ACTION_RESULT_FIELDS,
  extractRecordParams(contracts, "SqlOSHeadlessActionResult").map(camelCase),
);

const mappedPosts = [...endpoints.matchAll(/headless\.MapPost\("([^"]+)"/g)].map((match) => match[1]);
const mappedGets = [...endpoints.matchAll(/headless\.MapGet\("([^"]+)"/g)].map((match) => match[1]);
assertSame("headless POST paths", HEADLESS_ACTION_PATHS, mappedPosts);
assertSame("headless GET paths", HEADLESS_GET_PATHS, mappedGets);

const viewCases = [...service.matchAll(/"([a-z0-9-]+)"\s*=>\s*"\1"/g)].map((match) => match[1]);
for (const view of HEADLESS_VIEWS.filter((view) => view !== "login")) {
  if (!viewCases.includes(view)) {
    errors.push(`NormalizeView is missing ${view}`);
  }
}
for (const view of viewCases) {
  if (!HEADLESS_VIEWS.includes(view)) {
    errors.push(`HEADLESS_VIEWS is missing ${view}`);
  }
}

if (!HEADLESS_ACTION_RESULT_TYPES.includes("view") || !HEADLESS_ACTION_RESULT_TYPES.includes("redirect")) {
  errors.push("HEADLESS_ACTION_RESULT_TYPES must include view and redirect");
}

if (errors.length > 0) {
  console.error(`Headless contract drift:\n- ${errors.join("\n- ")}`);
  process.exit(1);
}

console.log("Headless contract matches SqlOSHeadlessAuthContracts, endpoints, and NormalizeView.");
