// Textual reader for packages/headless/src/contract.ts. Shared by the package
// drift check (verify-headless-contract.mjs) and the repo docs validator so
// there is exactly one parser for the contract file.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
export const contractTsPath = path.join(packageRoot, "src/contract.ts");

function extractArray(source, exportName, errors) {
  const match = source.match(new RegExp(`export const ${exportName} = \\[([\\s\\S]*?)\\] as const`));
  if (!match) {
    errors.push(`export ${exportName} not found in contract.ts`);
    return [];
  }
  return [...match[1].matchAll(/"([^"]+)"/g)].map((item) => item[1]);
}

function extractObjectBody(source, exportName, errors) {
  const match = source.match(new RegExp(`export const ${exportName} = \\{([\\s\\S]*?)\\} as const`));
  if (!match) {
    errors.push(`export ${exportName} not found in contract.ts`);
    return "";
  }
  return match[1];
}

/** `{ "key": ["a", "b"], ... }` → Map<key, string[]> */
function extractArrayMap(source, exportName, errors) {
  const body = extractObjectBody(source, exportName, errors);
  const result = new Map();
  for (const entry of body.matchAll(/"([^"]+)":\s*\[([^\]]*)\]/g)) {
    result.set(entry[1], [...entry[2].matchAll(/"([^"]+)"/g)].map((item) => item[1]));
  }
  return result;
}

/** `{ "key": "value", ... }` → Map<key, string> */
function extractStringMap(source, exportName, errors) {
  const body = extractObjectBody(source, exportName, errors);
  const result = new Map();
  for (const entry of body.matchAll(/"([^"]+)":\s*"([^"]+)"/g)) {
    result.set(entry[1], entry[2]);
  }
  return result;
}

export function readHeadlessContract(errors = []) {
  const source = fs.readFileSync(contractTsPath, "utf8");
  return {
    views: extractArray(source, "HEADLESS_VIEWS", errors),
    actionPaths: extractArray(source, "HEADLESS_ACTION_PATHS", errors),
    getPaths: extractArray(source, "HEADLESS_GET_PATHS", errors),
    actionResultTypes: extractArray(source, "HEADLESS_ACTION_RESULT_TYPES", errors),
    viewModelFields: extractArray(source, "HEADLESS_VIEW_MODEL_FIELDS", errors),
    actionResultFields: extractArray(source, "HEADLESS_ACTION_RESULT_FIELDS", errors),
    requestFields: extractArrayMap(source, "HEADLESS_REQUEST_FIELDS", errors),
    dtoFields: extractArrayMap(source, "HEADLESS_DTO_FIELDS", errors),
    credentialTypes: extractArray(source, "HEADLESS_CREDENTIAL_TYPES", errors),
    credentialRuntimeFlags: extractStringMap(source, "HEADLESS_CREDENTIAL_RUNTIME_FLAGS", errors),
  };
}
